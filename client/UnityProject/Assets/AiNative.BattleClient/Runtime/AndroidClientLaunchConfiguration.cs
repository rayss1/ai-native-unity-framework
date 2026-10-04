using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AiNative.Client.Fantasy;
using UnityEngine;

namespace AiNative.Client.Application
{
    /// <summary>Public Android launch options. Account credentials never enter this configuration.</summary>
    public sealed class AndroidClientLaunchConfiguration
    {
        private static readonly string[] IntentKeys = { "ainative.endpoint", "ainative.pin", "ainative.runId", "ainative.timeout", "ainative.automated" };
        public GateConnectionOptions GateOptions { get; private set; }
        public bool Automated { get; private set; }
        public string RunId { get; private set; }
        public float TimeoutSeconds { get; private set; }
        public string ResultPath { get; private set; }

        public static AndroidClientLaunchConfiguration Parse(IReadOnlyDictionary<string, string> extras, string persistentDataPath)
        {
            if (extras == null) throw new ArgumentNullException(nameof(extras));
            if (string.IsNullOrWhiteSpace(persistentDataPath)) throw new ArgumentException("Persistent storage is required.", nameof(persistentDataPath));
            string endpoint = Read(extras, "ainative.endpoint", "https://127.0.0.1:23001");
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri) || uri.Scheme != "https" || string.IsNullOrEmpty(uri.Host) ||
                uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Android Gate endpoint must be an HTTPS host and port without credentials, path or query.");
            string runId = Read(extras, "ainative.runId", Guid.NewGuid().ToString("N"));
            if (runId.Length == 0 || runId.Length > 64) throw new ArgumentException("Run ID must contain 1 to 64 letters, digits, underscores or hyphens.");
            foreach (char c in runId)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_' && c != '-')
                    throw new ArgumentException("Run ID contains an invalid character.");
            if (!float.TryParse(Read(extras, "ainative.timeout", "120"), NumberStyles.Float, CultureInfo.InvariantCulture, out float timeout) ||
                float.IsNaN(timeout) || float.IsInfinity(timeout) || timeout < 30 || timeout > 3600)
                throw new ArgumentException("Android timeout must be between 30 and 3600 seconds.");
            if (!bool.TryParse(Read(extras, "ainative.automated", "false"), out bool automated))
                throw new ArgumentException("Automated validation requires an explicit true or false value.");
            return new AndroidClientLaunchConfiguration {
                GateOptions = new GateConnectionOptions(uri.Host, uri.Port, true, Read(extras, "ainative.pin", "")),
                RunId = runId, Automated = automated, TimeoutSeconds = timeout,
                ResultPath = Path.Combine(persistentDataPath, "ainative-android-" + runId + ".json")
            };
        }

        public static AndroidClientLaunchConfiguration ReadCurrentIntent()
        {
            var extras = new Dictionary<string, string>();
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (AndroidJavaObject intent = activity.Call<AndroidJavaObject>("getIntent"))
            {
                foreach (string key in IntentKeys)
                    if (intent.Call<bool>("hasExtra", key)) extras[key] = intent.Call<string>("getStringExtra", key) ?? "";
            }
#endif
            return Parse(extras, global::UnityEngine.Application.persistentDataPath);
        }
        private static string Read(IReadOnlyDictionary<string, string> extras, string key, string fallback) => extras.TryGetValue(key, out string value) ? value ?? "" : fallback;
    }
}
