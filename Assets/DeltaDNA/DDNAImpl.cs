//
// Copyright (c) 2018 deltaDNA Ltd. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace DeltaDNA {

    using JSONObject = Dictionary<string, object>;

    internal class DDNAImpl : DDNABase {

        private readonly EventStore eventStore = null;
        private readonly EngageCache engageCache = null;
        private readonly ActionStore actionStore = null;

        private bool started = false;
        private bool uploading = false;
        private DateTime lastActive = DateTime.MinValue;

        private GameEvent launchNotificationEvent = null;

        private string pushNotificationToken = null;
        private string androidRegistrationId = null;

        private ReadOnlyCollection<string> whitelistDps =
            new ReadOnlyCollection<string>(new List<string>());
        private ReadOnlyCollection<string> whitelistEvents =
            new ReadOnlyCollection<string>(new List<string>());
        private Dictionary<string, ReadOnlyCollection<EventTrigger>> eventTriggers =
            new Dictionary<string, ReadOnlyCollection<EventTrigger>>();
        private ReadOnlyCollection<string> cacheImages =
            new ReadOnlyCollection<string>(new List<string>());

        internal DDNAImpl(DDNA ddna) : base(ddna) {
            string eventStorePath = null;
            if (Settings.UseEventStore) {
                eventStorePath = Settings.EVENT_STORAGE_PATH
                    .Replace("{persistent_path}", Application.persistentDataPath);
                if (!Utils.IsDirectoryWritable(eventStorePath)) {
                    Logger.LogWarning("Event store path unwritable, event caching disabled.");
                    Settings.UseEventStore = false;
                }
            }

            eventStore = new EventStore(eventStorePath);
            if (Settings.UseEventStore && !eventStore.IsInitialised) {
                // failed to access files for some reason
                Logger.LogWarning("Failed to access event store path, event caching disabled.");
                Settings.UseEventStore = false;
                eventStore = new EventStore(eventStorePath);
            }
            engageCache = new EngageCache(Settings);
            actionStore = new ActionStore(Settings.ACTIONS_STORAGE_PATH
                .Replace("{persistent_path}", Application.persistentDataPath));
            ImageMessageStore = new ImageMessageStore(ddna);

            #if DDNA_SMARTADS
            // initialise SmartAds so it can register for events
            var smartAds = SmartAds.Instance.Config(engageCache);
            smartAds.transform.parent = gameObject.transform;

            EngageFactory = new EngageFactory(this, smartAds);

            Application.RequestAdvertisingIdentifierAsync(
                (string advertisingId, bool trackingEnabled, string error) => {
                    if (trackingEnabled) {
                        PlayerPrefs.SetString(DDNA.PF_KEY_ADVERTISING_ID, advertisingId);
                    }
                }
            );

            #else
            EngageFactory = new EngageFactory(this, null);
            #endif
        }

        #region Unity Lifecycle

        override internal void OnApplicationPause(bool pauseStatus) {
            if (pauseStatus) {
                lastActive = DateTime.UtcNow;
                eventStore.FlushBuffers();
                engageCache.Save();
            } else {
                var backgroundSeconds = (DateTime.UtcNow - lastActive).TotalSeconds;
                if (backgroundSeconds > Settings.SessionTimeoutSeconds) {
                    lastActive = DateTime.MinValue;
                    NewSession();
                }
            }
        }

        override internal void OnDestroy() {
            if (eventStore != null) {
                eventStore.FlushBuffers();
                eventStore.Dispose();
            }
        }

        #endregion
        #region Client Interface

        override internal void StartSDK(bool newPlayer) {
            started = true;

            if (newPlayer) {
                engageCache.Clear();
                actionStore.Clear();
            }

            NewSession();

            if (launchNotificationEvent != null) {
                RecordEvent(launchNotificationEvent);
                launchNotificationEvent = null;
            }

            TriggerDefaultEvents(newPlayer);

            // setup automated event uploads
            if (Settings.BackgroundEventUpload && !IsInvoking("Upload")) {
                InvokeRepeating(
                    "Upload",
                    Settings.BackgroundEventUploadStartDelaySeconds,
                    Settings.BackgroundEventUploadRepeatRateSeconds);
            }
        }

        override internal void StopSDK() {
            if (started) {
                Logger.LogInfo("Stopping DDNA SDK");

                RecordEvent("gameEnded");
                CancelInvoke();
                Upload();

                started = false;
            } else {
                Logger.LogDebug("SDK not running");
            }
        }

        override internal EventAction RecordEvent<T>(T gameEvent) {
            if (!started) {
                throw new Exception("You must first start the SDK via the StartSDK method");
            } else if (whitelistEvents.Count != 0 && !whitelistEvents.Contains(gameEvent.Name)) {
                Logger.LogDebug("Event " + gameEvent.Name + " is not whitelisted, ignoring");
                return EventAction.CreateEmpty(gameEvent as GameEvent);
            }

            gameEvent.AddParam("platform", Platform);
            gameEvent.AddParam("sdkVersion", Settings.SDK_VERSION);

            var eventSchema = gameEvent.AsDictionary();
            eventSchema["userID"] = this.UserID;
            eventSchema["sessionID"] = this.SessionID;
            eventSchema["eventUUID"] = Guid.NewGuid().ToString();

            string currentTimestmp = GetCurrentTimestamp();
            if (currentTimestmp != null) {
                eventSchema["eventTimestamp"] = GetCurrentTimestamp();
            }

            try {
                string json = MiniJSON.Json.Serialize(eventSchema);
                if (!this.eventStore.Push(json)) {
                    Logger.LogWarning("Event store full, dropping '"+gameEvent.Name+"' event.");
                }
            } catch (Exception ex) {
                Logger.LogWarning("Unable to generate JSON for '"+gameEvent.Name+"' event. "+ex.Message);
            }

            return new EventAction(
                gameEvent as GameEvent,
                eventTriggers.ContainsKey(gameEvent.Name)
                    ? eventTriggers[gameEvent.Name]
                    : EventAction.EMPTY_TRIGGERS,
                actionStore);
        }

        override internal EventAction RecordEvent(string eventName) {
            return RecordEvent(new GameEvent(eventName));
        }

        override internal EventAction RecordEvent(string eventName, Dictionary<string, object> eventParams) {
            var gameEvent = new GameEvent(eventName);
            foreach (var key in eventParams.Keys) {
                gameEvent.AddParam(key, eventParams[key]);
            }
            return RecordEvent(gameEvent);
        }

        override internal void RequestEngagement(Engagement engagement, Action<Dictionary<string, object>> callback) {
            if (!this.started) {
                throw new Exception("You must first start the SDK via the StartSDK method.");
            } else if (whitelistDps.Count != 0
                && !whitelistDps.Contains(engagement.GetDecisionPointAndFlavour())) {
                Logger.LogDebug(string.Format(
                    "Decision point {0} is not whitelisted",
                    engagement.GetDecisionPointAndFlavour()));
                engagement.StatusCode = 200;
                engagement.Raw = "{}";
                callback(engagement.JSON);
                return;
            }

            if (String.IsNullOrEmpty(this.EngageURL)) {
                throw new Exception("Engage URL not configured.");
            }

            try {

                var dict = engagement.AsDictionary();

                var request = new EngageRequest(dict["decisionPoint"] as string);
                request.Flavour = dict["flavour"] as string;
                request.Parameters = dict["parameters"] as Dictionary<string, object>;

                EngageResponse handler = (string response, int statusCode, string error) => {
                    JSONObject responseJSON = new JSONObject();
                    if (response != null) {
                        try {
                            responseJSON = DeltaDNA.MiniJSON.Json.Deserialize(response) as JSONObject;
                        } catch (Exception exception) {
                            Logger.LogError("Engagement "+engagement.DecisionPoint+" responded with invalid JSON: "+exception.Message);
                        }
                    }
                    callback(responseJSON);
                };

                StartCoroutine(Engage.Request(ddna, engageCache, request, handler));
            } catch (Exception ex) {
                Logger.LogWarning("Engagement request failed: "+ex.Message);
            }
        }

        override internal void RequestEngagement(Engagement engagement, Action<Engagement> onCompleted, Action<Exception> onError) {
            if (!this.started) {
                throw new Exception("You must first start the SDK via the StartSDK method.");
            } else if (whitelistDps.Count != 0
                && !whitelistDps.Contains(engagement.GetDecisionPointAndFlavour())) {
                Logger.LogDebug(string.Format(
                    "Decision point {0} is not whitelisted",
                    engagement.GetDecisionPointAndFlavour()));
                engagement.StatusCode = 200;
                engagement.Raw = "{}";
                onCompleted(engagement);
                return;
            }

            if (String.IsNullOrEmpty(this.EngageURL)) {
                throw new Exception("Engage URL not configured.");
            }

            try {
                var dict = engagement.AsDictionary();

                var request = new EngageRequest(dict["decisionPoint"] as string);
                request.Flavour = dict["flavour"] as string;
                request.Parameters = dict["parameters"] as Dictionary<string, object>;

                EngageResponse handler = (string response, int statusCode, string error) => {
                    engagement.Raw = response;
                    engagement.StatusCode = statusCode;
                    engagement.Error = error;

                    onCompleted(engagement);
                };

                StartCoroutine(Engage.Request(ddna, engageCache, request, handler));
            } catch (Exception ex) {
                Logger.LogWarning("Engagement request failed: "+ex.Message);
            }
        }

        override internal void RecordPushNotification(Dictionary<string, object> payload) {
            Logger.LogDebug("Received push notification: "+payload);

            var notificationEvent = new GameEvent("notificationOpened");
            try {
                if (payload.ContainsKey("_ddId")) {
                    notificationEvent.AddParam("notificationId", Convert.ToInt64(payload["_ddId"]));
                }
                if (payload.ContainsKey("_ddName")) {
                    notificationEvent.AddParam("notificationName", payload["_ddName"]);
                }

                bool insertCommunicationAttrs = false;
                if (payload.ContainsKey("_ddCampaign")) {
                    notificationEvent.AddParam("campaignId", Convert.ToInt64(payload["_ddCampaign"]));
                    insertCommunicationAttrs = true;
                }
                if (payload.ContainsKey("_ddCohort")) {
                    notificationEvent.AddParam("cohortId", Convert.ToInt64(payload["_ddCohort"]));
                    insertCommunicationAttrs = true;
                }
                if (insertCommunicationAttrs && payload.ContainsKey("_ddCommunicationSender")) {
                    // _ddCommunicationSender inserted by respective native notification SDK
                    notificationEvent.AddParam("communicationSender", payload["_ddCommunicationSender"]);
                    notificationEvent.AddParam("communicationState", "OPEN");
                }

                if (payload.ContainsKey("_ddLaunch")) {
                    // _ddLaunch inserted by respective native notification SDK
                    notificationEvent.AddParam("notificationLaunch", Convert.ToBoolean(payload["_ddLaunch"]));
                }
                if (payload.ContainsKey("_ddCampaign")) {
                    notificationEvent.AddParam("campaignId", Convert.ToInt64(payload["_ddCampaign"]));
                }
                if (payload.ContainsKey("_ddCohort")) {
                    notificationEvent.AddParam("cohortId", Convert.ToInt64(payload["_ddCohort"]));
                }
                notificationEvent.AddParam("communicationState", "OPEN");
            } catch (Exception ex) {
                Logger.LogError("Error parsing push notification payload. "+ex.Message);
            }

            if (this.started) {
                this.RecordEvent(notificationEvent);
            } else {
                this.launchNotificationEvent = notificationEvent;
            }
        }

        override internal void RequestSessionConfiguration() {
            Logger.LogDebug("Requesting session configuration");

            var firstSession = PlayerPrefs.HasKey(DDNA.PF_KEY_FIRST_SESSION)
                ? DateTime.ParseExact(
                    PlayerPrefs.GetString(DDNA.PF_KEY_FIRST_SESSION),
                    Settings.EVENT_TIMESTAMP_FORMAT,
                    CultureInfo.InvariantCulture)
                : (DateTime?) null;
            var lastSession = PlayerPrefs.HasKey(DDNA.PF_KEY_LAST_SESSION)
                ? DateTime.ParseExact(
                    PlayerPrefs.GetString(DDNA.PF_KEY_LAST_SESSION),
                    Settings.EVENT_TIMESTAMP_FORMAT,
                    CultureInfo.InvariantCulture)
                : (DateTime?) null;

            RequestEngagement(
                new Engagement("config") { Flavour = "internal" }
                    .AddParam("timeSinceFirstSession", firstSession != null
                        ? ((TimeSpan) (DateTime.UtcNow - firstSession)).TotalMilliseconds
                        : 0)
                    .AddParam("timeSinceLastSession", lastSession != null
                        ? ((TimeSpan) (DateTime.UtcNow - lastSession)).TotalMilliseconds
                        : 0),
                HandleSessionConfigurationCallback);
        }

        override internal void Upload() {
            if (!started) {
                Logger.LogError("You must first start the SDK via the StartSDK method.");
                return;
            }

            if (IsUploading) {
                Logger.LogWarning("Event upload already in progress, try again later.");
                return;
            }

            StartCoroutine(UploadCoroutine());
        }

        override internal void DownloadImageAssets() {
            Logger.LogDebug("Downloading image assets");

            StartCoroutine(ImageMessageStore.Prefetch(
                () => {
                    Logger.LogDebug("Image cache populated");
                    ddna.NotifyOnImageCachePopulated();
                },
                e => {
                    Logger.LogDebug("Image caching failed due to " + e);
                    ddna.NotifyOnImageCachingFailed(e);
                },
                cacheImages.ToArray()));
        }

        override internal void ClearPersistentData() {
            if (eventStore != null) eventStore.ClearAll();
            if (engageCache != null) engageCache.Clear();
            if (actionStore != null) actionStore.Clear();
            if (ImageMessageStore != null) ImageMessageStore.Clear();
        }

        internal override void ForgetMe() {
            if (HasStarted) StopSDK();
        }

        #endregion
        #region Properties

        override internal bool HasStarted { get { return started; }}
        override internal bool IsUploading { get { return uploading; }}

        #endregion
        #region Client Configuration

        override internal string CrossGameUserID {
            get { return PlayerPrefs.GetString(DDNA.PF_KEY_CROSS_GAME_USER_ID, null); }

            set {
                if (String.IsNullOrEmpty(value)) {
                    Logger.LogWarning("CrossGameUserID cannot be null or empty");
                } else if (value != CrossGameUserID) {
                    PlayerPrefs.SetString(DDNA.PF_KEY_CROSS_GAME_USER_ID, value);

                    if (started) {
                        RecordEvent(new GameEvent("ddnaRegisterCrossGameUserID")
                            .AddParam("ddnaCrossGameUserID", value));
                    } // else send with gameStarted event
                }
            }
        }

        override internal string AndroidRegistrationID {
            get { return androidRegistrationId; }
            set {
                if (!String.IsNullOrEmpty(value) && value != androidRegistrationId) {
                    var notificationServicesEvent = new GameEvent("notificationServices")
                        .AddParam("androidRegistrationID", value);

                    if (started) {
                        RecordEvent(notificationServicesEvent);
                    } // else send with gameStarted event
                    androidRegistrationId = value;
                }
            }
        }

        override internal string PushNotificationToken {
            get { return pushNotificationToken; }
            set {
                if (!String.IsNullOrEmpty(value) && value != pushNotificationToken) {
                    var notificationServicesEvent = new GameEvent("notificationServices")
                        .AddParam("pushNotificationToken", value);

                    if (started) {
                        RecordEvent(notificationServicesEvent);
                    } // else send with gameStarted event
                    pushNotificationToken = value;
                }
            }
        }

        #endregion
        #region Helpers

        private IEnumerator UploadCoroutine() {
            uploading = true;

            try {
                // Swap over event queue.
                this.eventStore.Swap();

                // Create bulk event message to post.
                List<string> events = eventStore.Read();

                if (events != null && events.Count > 0)
                {
                    Logger.LogDebug("Starting event upload.");

                    Action<bool, int> postCb = (succeeded, statusCode) =>
                    {
                        if (succeeded)
                        {
                            Logger.LogDebug("Event upload successful.");
                            this.eventStore.ClearOut();
                        }
                        else if (statusCode == 400) {
                            Logger.LogDebug("Collect rejected events, possible corruption.");
                            this.eventStore.ClearOut();
                        }
                        else {
                            Logger.LogWarning("Event upload failed - try again later.");
                        }
                    };

                    yield return StartCoroutine(PostEvents(events.ToArray(), postCb));
                }
            } finally {
                uploading = false;
            }
        }

        private IEnumerator PostEvents(string[] events, Action<bool, int> resultCallback)
        {
            string bulkEvent = "{\"eventList\":[" + String.Join(",", events) + "]}";
            string url;
            if (HashSecret != null) {
                string md5Hash = DDNA.GenerateHash(bulkEvent, this.HashSecret);
                url = DDNA.FormatURI(Settings.COLLECT_HASH_URL_PATTERN, this.CollectURL, this.EnvironmentKey, md5Hash);
            } else {
                url = DDNA.FormatURI(Settings.COLLECT_URL_PATTERN, this.CollectURL, this.EnvironmentKey, null);
            }

            int attempts = 0;
            bool succeeded = false;
            int status = 0;

            Action<int, string, string> completionHandler = (statusCode, data, error) => {
                if (statusCode > 0 && statusCode < 400) {
                    succeeded = true;
                }
                else {
                    Logger.LogDebug("Error posting events: "+error+" "+data);
                }
                status = statusCode;
            };

            HttpRequest request = new HttpRequest(url);
            request.HTTPMethod = HttpRequest.HTTPMethodType.POST;
            request.HTTPBody = bulkEvent;
            request.setHeader("Content-Type", "application/json");

            do {
                yield return StartCoroutine(Network.SendRequest(request, completionHandler));

                if (succeeded || ++attempts < Settings.HttpRequestMaxRetries) break;

                yield return new WaitForSeconds(Settings.HttpRequestRetryDelaySeconds);
            } while (attempts < Settings.HttpRequestMaxRetries);

            resultCallback(succeeded, status);
        }

        private void TriggerDefaultEvents(bool newPlayer)
        {
            if (Settings.OnFirstRunSendNewPlayerEvent && newPlayer)
            {
                Logger.LogDebug("Sending 'newPlayer' event");

                var newPlayerEvent = new GameEvent("newPlayer");
                if (ClientInfo.CountryCode != null) {
                    newPlayerEvent.AddParam("userCountry", ClientInfo.CountryCode);
                }

                RecordEvent(newPlayerEvent);
            }

            if (Settings.OnInitSendGameStartedEvent)
            {
                Logger.LogDebug("Sending 'gameStarted' event");

                var gameStartedEvent = new GameEvent("gameStarted")
                    .AddParam("clientVersion", this.ClientVersion)
                    .AddParam("userLocale", ClientInfo.Locale);

                if (!string.IsNullOrEmpty(CrossGameUserID)) {
                    gameStartedEvent.AddParam("ddnaCrossGameUserID", CrossGameUserID);
                }

                if (!String.IsNullOrEmpty(this.PushNotificationToken)) {
                    gameStartedEvent.AddParam("pushNotificationToken", this.PushNotificationToken);
                }

                if (!String.IsNullOrEmpty(this.AndroidRegistrationID)) {
                    gameStartedEvent.AddParam("androidRegistrationID", this.AndroidRegistrationID);
                }

                RecordEvent(gameStartedEvent);
            }

            if (Settings.OnInitSendClientDeviceEvent)
            {
                Logger.LogDebug("Sending 'clientDevice' event");

                var clientDeviceEvent = new GameEvent("clientDevice")
                    .AddParam("deviceName", ClientInfo.DeviceName)
                    .AddParam("deviceType", ClientInfo.DeviceType)
                    .AddParam("hardwareVersion", ClientInfo.DeviceModel)
                    .AddParam("operatingSystem", ClientInfo.OperatingSystem)
                    .AddParam("operatingSystemVersion", ClientInfo.OperatingSystemVersion)
                    .AddParam("timezoneOffset", ClientInfo.TimezoneOffset)
                    .AddParam("userLanguage", ClientInfo.LanguageCode);

                if (ClientInfo.Manufacturer != null) {
                    clientDeviceEvent.AddParam("manufacturer", ClientInfo.Manufacturer);
                }

                RecordEvent(clientDeviceEvent);
            }
        }

        private void HandleSessionConfigurationCallback(JSONObject response) {
            if (response.Count > 0) {
                var parameters = response["parameters"];

                if (parameters != null && parameters is JSONObject) {
                    object dpWhitelist = null;
                    (parameters as JSONObject).TryGetValue("dpWhitelist", out dpWhitelist);
                    if (dpWhitelist != null && dpWhitelist is List<object>) {
                        whitelistDps = new ReadOnlyCollection<string>(
                            (dpWhitelist as List<object>)
                            .Select(e => e as string)
                            .ToList());
                    }

                    object eventsWhitelist = null;
                    (parameters as JSONObject).TryGetValue("eventsWhitelist", out eventsWhitelist);
                    if (eventsWhitelist != null && eventsWhitelist is List<object>) {
                        whitelistEvents = new ReadOnlyCollection<string>(
                            (eventsWhitelist as List<object>)
                            .Select(e => e as string)
                            .ToList());
                    }

                    object triggers = null;
                    (parameters as JSONObject).TryGetValue("triggers", out triggers);
                    if (triggers != null && triggers is List<object>) {
                        eventTriggers = (triggers as List<object>)
                            .Select((e, i) => {
                                var t = new EventTrigger(this, i, e as JSONObject);

                                // save persistent actions
                                var p = t.GetResponse().GetOrDefault("parameters", new JSONObject());
                                if (p.GetOrDefault("ddnaIsPersistent", false)) {
                                    actionStore.Put(t, p);
                                }

                                return t;
                            })
                            .GroupBy(e => e.GetEventName())
                            .ToDictionary(e => e.Key, e => {
                                var list = e.ToList();
                                list.Sort();
                                return new ReadOnlyCollection<EventTrigger>(list);
                            });
                    }

                    object imageCache = null;
                    (parameters as JSONObject).TryGetValue("imageCache", out imageCache);
                    if (imageCache != null && imageCache is List<object>) {
                        cacheImages = new ReadOnlyCollection<string>(
                            (imageCache as List<object>)
                            .Select(e => e as string)
                            .ToList());
                        DownloadImageAssets();
                    }

                    #if DDNA_SMARTADS
                    SmartAds.Instance.RegisterForAdsInternal(response);
                    #endif

                    Logger.LogDebug("Session configured");
                    object cached = null;
                    (parameters as JSONObject).TryGetValue("isCachedResponse", out cached);
                    if (cached != null && cached is bool) {
                        ddna.NotifyOnSessionConfigured(cached as bool? ?? false);
                    } else {
                        ddna.NotifyOnSessionConfigured(false);
                    }
                }
            } else {
                Logger.LogWarning("Session configuration failed");
                ddna.NotifyOnSessionConfigurationFailed();
            }
        }

        #endregion
    }
}
