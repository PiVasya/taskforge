using System.Security.Claims;
using System.Text;
using System.Text.Json;
using LearningContentService.Data;
using LearningContentService.Data.Entities;
using LearningContentService.DTO;
using LearningContentService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using static LearningContentService.Services.Common.LearningContentCommonService;
using static LearningContentService.Services.Serialization.LearningContentSerializationService;

namespace LearningContentService.Endpoints;

internal static partial class LearningContentEndpoints
{
    internal static WebApplication MapLearningContentEndpoints(this WebApplication app)
    {
        MapServiceInfoEndpoints(app);
        MapLearningEndpoints(app);
        MapAdminLearningEndpoints(app);

        return app;
    }
}
