using AutoMapper;
using ESPresense.Models;

namespace ESPresense.Services;

public class MappingProfile : Profile
{
    public MappingProfile()
    {
        CreateMap<Node, NodeState>()
            .ForMember(dest => dest.Floors, opt => opt.MapFrom(a => a.Floors!.Select(b => b.Id).ToArray()))
            .ForMember(dest => dest.SourceType, opt => opt.MapFrom(src => src.SourceType));

        CreateMap<Node, NodeStateTele>()
            .ForMember(dest => dest.Floors, opt => opt.MapFrom(a => a.Floors!.Select(b => b.Id).ToArray()))
            .ForMember(dest => dest.SourceType, opt => opt.MapFrom(src => src.SourceType))
            .AfterMap<NodeToNodeStateTeleAction>();
    }
}
