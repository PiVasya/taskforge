using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Http.Features;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TaskForge.Files.Api.Data;
using TaskForge.Files.Api.Domain;


namespace TaskForge.Files.Api.Contracts;

public sealed record ImageValidationResult(bool Ok, string? ContentType, string? Extension, string? Code, string? Message);
