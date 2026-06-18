using Microsoft.EntityFrameworkCore;
using TaskForge.Notifications.Api.Data;
using TaskForge.Notifications.Api.Domain;


namespace TaskForge.Notifications.Api.Contracts;

public sealed record NotificationRequest(Guid? UserId, string? Type, string? Title, string? Message);
