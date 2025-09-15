using AutoMapper;
using ESPresense.Models;

namespace ESPresense.Services;

public class MappingProfile : Profile
{
    /// <summary>
    /// Configures AutoMapper mappings for node-related DTOs.
    /// </summary>
    /// <remarks>
    /// - Maps Node -> NodeState:
    ///   - Floors is populated from Node.Floors by projecting each Floor.Id into an array.
    ///   - SourceType is copied from Node.SourceType.
    /// - Maps Node -> NodeStateTele:
    ///   - Floors and SourceType are mapped as above.
    ///   - Runs <c>NodeToNodeStateTeleAction</c> in an <c>AfterMap</c> step to apply additional transformations.
    /// The mapping uses a null-forgiving access to <c>Node.Floors</c> when projecting floor Ids.
    /// </remarks>
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
