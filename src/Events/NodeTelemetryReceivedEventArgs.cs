﻿using ESPresense.Models;

namespace ESPresense.Events;

public class NodeTelemetryReceivedEventArgs
{
    public string? NodeId { get; set; }
    public NodeTelemetry? Payload { get; set; }
}