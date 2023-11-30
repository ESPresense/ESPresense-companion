using AutoMapper;
using ESPresense.Models;

namespace ESPresense.Services;

public class MappingService
{
    public MappingService(NodeTelemetryStore nts, FirmwareTypeStore fs)
    {
        Mapper = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Node, NodeState>()
                .ForMember(dest => dest.Floors, opt => opt.MapFrom(a => a.Floors!.Select(b => b.Id).ToArray()));
            cfg.CreateMap<Node, NodeStateTele>()
                .ForMember(dest => dest.Floors, opt => opt.MapFrom(a => a.Floors!.Select(b => b.Id).ToArray()))
                .AfterMap((src, dest) =>
                {
                    var tele = dest.Telemetry = nts.Get(src.Id ?? "");
                    if (tele != null)
                    {
                        dest.Flavor = fs.GetFlavor(tele.Firmware);
                        dest.CPU = fs.GetCpu(tele.Firmware);
                    }
                })
                .AfterMap((src, dest) => dest.Online = nts.Online(src.Id ?? ""));
        }).CreateMapper();
    }

    public readonly IMapper Mapper;

}