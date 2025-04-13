using ESPresense.Models;
using Serilog;

namespace ESPresense.Services
{
    public static class MqttCoordinatorExtensions
    {
        public static async Task UpdateSetting(this MqttCoordinator mqtt, string id, string setting, string? value, bool retain, string? oldValue)
        {
            // Only log and send if values are different
            if (value != oldValue)
            {
                Log.Information("Updating {NodeId} {Setting}: {OldValue} -> {NewValue}",
                    id,
                    setting,
                    string.IsNullOrEmpty(oldValue) ? "(empty)" : oldValue,
                    string.IsNullOrEmpty(value) ? "(empty)" : value);

                await mqtt.EnqueueAsync($"espresense/rooms/{id}/{setting}/set", value, retain);
            }
        }

        public static async Task UpdateSetting(this MqttCoordinator mqtt, string id, string setting, bool? value, bool retain, bool? oldValue)
        {
            // Convert to ON/OFF string format for MQTT
            string? strValue = value.HasValue ? (value.Value ? "ON" : "OFF") : null;
            string? strOldValue = oldValue.HasValue ? (oldValue.Value ? "ON" : "OFF") : null;

            await mqtt.UpdateSetting(id, setting, strValue, retain, strOldValue);
        }

        public static async Task UpdateSetting(this MqttCoordinator mqtt, string id, string setting, int? value, bool retain, int? oldValue)
        {
            // Convert to string for comparison and mqtt
            string? strValue = value?.ToString();
            string? strOldValue = oldValue?.ToString();

            // Special handling for 0 values in certain fields
            if ((setting == "rx_adj_rssi" || setting == "tx_ref_rssi") && value == 0)
            {
                strValue = "";
            }

            await mqtt.UpdateSetting(id, setting, strValue, retain, strOldValue);
        }

        public static async Task UpdateSetting(this MqttCoordinator mqtt, string id, string setting, double? value, bool retain, double? oldValue)
        {
            // Convert to string with proper formatting for comparison and mqtt
            string? strValue = value.HasValue ? $"{value:0.00}" : null;
            string? strOldValue = oldValue.HasValue ? $"{oldValue:0.00}" : null;

            // Special handling for 0 values in absorption
            if (setting == "absorption" && value == 0)
            {
                strValue = "";
            }

            await mqtt.UpdateSetting(id, setting, strValue, retain, strOldValue);
        }
    }
}