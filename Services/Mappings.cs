using AutoMapper;
using ESPresense.Models;

namespace ESPresense.Services;

static class Mappings
{
    private static readonly Lazy<MapperConfiguration> Config = new Lazy<MapperConfiguration>(() =>
    {
        return new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Node, NodeState>();
            cfg.CreateMap<Node, NodeTeleState>();
        });
    });

    public static IMapper Mapper => Config.Value.CreateMapper();
}