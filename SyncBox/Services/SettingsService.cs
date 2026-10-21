using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SyncBox.Services
{
    public sealed class AppSettings
    {
        public bool UseGpu { get; set; } = true;

        public bool UseWifi { get; set; }

        public bool CaptureWholeDesktop { get; set; } = true;

        public bool IsCinematicMode { get; set; } = true;

        public bool RequireAck { get; set; }

        public bool AutoDetectDevice { get; set; } = true;

        public bool AutoDetectWifi { get; set; } = true;

        public string WifiPassword { get; set; } = string.Empty;

        public int HorizontalLedCount { get; set; } = 19;

        public int VerticalLedCount { get; set; } = 10;

        public string ComPort { get; set; } = string.Empty;

        public int BaudRate { get; set; } = 1000000;

        public string IpAddress { get; set; } = "192.168.4.1";

        public int Port { get; set; } = 1234;

        public double Brightness { get; set; } = 0.85;

        public double Gamma { get; set; } = 1.0;

        public double Saturation { get; set; } = 1.35;

        public double Smoothing { get; set; } = 0.7;

        public double BarThreshold { get; set; } = 0.06;

        public int AverageSize { get; set; } = 3;

        public int TargetFps { get; set; } = 60;

        public AppSettings Sanitized()
        {
            HorizontalLedCount = LedLimits.Clamp(HorizontalLedCount);
            VerticalLedCount = LedLimits.Clamp(VerticalLedCount);
            BaudRate = LedLimits.ClampBaud(BaudRate);
            Port = LedLimits.IsValidPort(Port) ? Port : 1234;
            Brightness = Math.Clamp(Brightness, 0.0, 1.0);
            Gamma = Math.Clamp(Gamma, 0.2, 5.0);
            Saturation = Math.Clamp(Saturation, 0.0, 3.0);
            Smoothing = Math.Clamp(Smoothing, 0.0, 0.98);
            BarThreshold = Math.Clamp(BarThreshold, 0.0, 0.5);
            AverageSize = Math.Clamp(AverageSize, 1, 5);
            TargetFps = Math.Clamp(TargetFps, 0, 240);
            ComPort = (ComPort ?? string.Empty).Trim();
            IpAddress = (IpAddress ?? string.Empty).Trim();
            WifiPassword = WifiPassword ?? string.Empty;
            if (WifiPassword.Length > 128)
            {
                WifiPassword = WifiPassword[..128];
            }

            return this;
        }
    }

    public sealed class SettingsService
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        private readonly string _filePath;

        public SettingsService(string? filePath = null)
        {
            _filePath = filePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SyncBox",
                "settings.json");
        }

        public string FilePath => _filePath;

        public AppSettings Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return new AppSettings();
                }

                string json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new AppSettings();
                }

                return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions)?.Sanitized() ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public bool TrySave(AppSettings settings)
        {
            try
            {
                string? directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonSerializer.Serialize(settings.Sanitized(), SerializerOptions);
                string temporary = _filePath + ".tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, _filePath, overwrite: true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}