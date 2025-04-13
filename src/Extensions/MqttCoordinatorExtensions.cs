using ESPresense.Models;
using Serilog;

namespace ESPresense.Services
{
    public static class MqttCoordinatorExtensions
    {
        /// <summary>
        /// Updates the specified setting via MQTT when a change in value is detected.
        /// </summary>
        /// <remarks>
        /// Compares the new and current setting values and, if they differ, logs the update and enqueues a message to update the setting on the MQTT broker. The MQTT topic is constructed using the provided identifier and setting name.
        /// </remarks>
        /// <param name="id">The identifier of the target node (e.g., room ID).</param>
        /// <param name="setting">The name of the setting to update.</param>
        /// <param name="value">The new string value for the setting; treats null or empty as an empty value.</param>
        /// <param name="retain">Indicates whether the MQTT message should be retained.</param>
        /// <param name="oldValue">The current string value of the setting; treats null or empty as an empty value.</param>
        /// <returns>An asynchronous task representing the update operation.</returns>
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

        /// <summary>
        /// Updates a setting on the MQTT coordinator by converting a boolean value to its MQTT string representation ("ON" or "OFF") and delegating the update.
        /// </summary>
        /// <param name="id">The identifier associated with the target setting.</param>
        /// <param name="setting">The name of the setting to update.</param>
        /// <param name="value">The new boolean value. True is converted to "ON", false to "OFF", and null indicates no value.</param>
        /// <param name="retain">Indicates whether the MQTT message should be retained by the broker.</param>
        /// <param name="oldValue">The previous boolean value for comparison, where null implies an unset value.</param>
        public static async Task UpdateSetting(this MqttCoordinator mqtt, string id, string setting, bool? value, bool retain, bool? oldValue)
        {
            // Convert to ON/OFF string format for MQTT
            string? strValue = value.HasValue ? (value.Value ? "ON" : "OFF") : null;
            string? strOldValue = oldValue.HasValue ? (oldValue.Value ? "ON" : "OFF") : null;

            await mqtt.UpdateSetting(id, setting, strValue, retain, strOldValue);
        }

        /// <summary>
        /// Asynchronously updates a setting using an integer value for MQTT transmission.
        /// </summary>
        /// <remarks>
        /// Converts the provided nullable integer to its string representation before updating the setting.
        /// For the "rx_adj_rssi" and "tx_ref_rssi" settings, a value of zero is treated as an empty string.
        /// </remarks>
        /// <param name="id">The identifier for the setting.</param>
        /// <param name="setting">The name of the setting to update.</param>
        /// <param name="value">The new integer value for the setting, or null if not specified.</param>
        /// <param name="retain">Indicates whether the update should be retained by the MQTT broker.</param>
        /// <param name="oldValue">The previous integer value of the setting for comparison.</param>
        /// <returns>A Task that represents the asynchronous update operation.</returns>
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

        /// <summary>
        /// Asynchronously updates an MQTT setting using a double value.
        /// </summary>
        /// <remarks>
        /// Converts the provided double value to a string formatted with two decimal places for MQTT transmission.
        /// For the "absorption" setting, a value of 0 is converted to an empty string.
        /// The update is dispatched only if the new value differs from the old value.
        /// </remarks>
        /// <param name="id">The identifier associated with the setting.</param>
        /// <param name="setting">The name of the setting to update.</param>
        /// <param name="value">The new double value, formatted to two decimal places if present.</param>
        /// <param name="retain">Indicates whether the MQTT broker should retain the update.</param>
        /// <param name="oldValue">The previous double value for change detection, formatted similarly if present.</param>
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