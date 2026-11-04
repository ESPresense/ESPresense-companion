using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ESPresense.Models;

/// <summary>
/// Marks the always-present <see cref="CalibrationCell"/> keys as required in the Swagger schema, leaving the
/// four that are omitted when unknown (<see cref="CalibrationCellKeys.Optional"/>) optional.
/// </summary>
public sealed class CalibrationCellSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type != typeof(CalibrationCell) || schema is not OpenApiSchema s) return;
        s.Required = new HashSet<string>(CalibrationCellKeys.All.Except(CalibrationCellKeys.Optional));
    }
}
