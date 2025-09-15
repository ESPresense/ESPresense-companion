namespace ESPresense.Models;

public enum NodeSourceType
{
    /// <summary>
    /// Node was dynamically discovered via MQTT
    /// </summary>
    Discovered,
    
    /// <summary>
    /// Node was defined in config.yaml
    /// </summary>
    Config
}