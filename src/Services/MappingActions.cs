using AutoMapper;
using ESPresense.Models;

namespace ESPresense.Services;

public class NodeToNodeStateTeleAction : IMappingAction<Node, NodeStateTele>
{
    private readonly NodeTelemetryStore _nts;
    private readonly FirmwareTypeStore _fs;

    public NodeToNodeStateTeleAction(NodeTelemetryStore nts, FirmwareTypeStore fs)
    {
        _nts = nts;
        _fs = fs;
    }

    public void Process(Node src, NodeStateTele dest, ResolutionContext context)
    {
        dest.Telemetry = _nts.Get(src.Id);
        if (dest.Telemetry != null)
        {
            dest.Flavor = _fs.GetFlavor(dest.Telemetry.Firmware);
            dest.CPU = _fs.GetCpu(dest.Telemetry.Firmware);
        }
        dest.Online = _nts.Online(src.Id);
    }
}

