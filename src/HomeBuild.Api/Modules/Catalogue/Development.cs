using HomeBuild.Domain;

namespace HomeBuild.Api.Modules.Catalogue;

internal sealed record Development(DevelopmentId Id, string Name, string Town, string DepositStrategy);
