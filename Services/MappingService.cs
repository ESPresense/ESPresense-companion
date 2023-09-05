using AutoMapper;
using ESPresense.Models;

namespace ESPresense.Services;

public class MappingService
{
    private readonly NodeTelemetryStore _nts;

    public MappingService(NodeTelemetryStore nts)
    {
        _nts = nts;
        Mapper = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Node, NodeState>()
                .ForMember(dest => dest.Floors, opt => opt.MapFrom(a => a.Floors.Select(a => a.Id).ToArray()));
            cfg.CreateMap<Node, NodeStateTele>()
                .ForMember(dest => dest.Floors, opt => opt.MapFrom(a => a.Floors.Select(a => a.Id).ToArray()))
                .AfterMap((src, dest) => dest.Telemetry = _nts.Get(src.Id ?? ""))
                .AfterMap((src, dest) => dest.Online = _nts.Online(src.Id ?? ""));
        }).CreateMapper();
    }


    public readonly IMapper Mapper;

}