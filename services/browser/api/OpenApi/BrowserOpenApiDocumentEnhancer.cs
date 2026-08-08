using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskForge.Browser.Api.OpenApi;

public sealed class BrowserOpenApiDocumentEnhancer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string Enhance(string source)
    {
        var root = JsonNode.Parse(source)?.AsObject()
                   ?? throw new InvalidOperationException("Generated OpenAPI document is not a JSON object.");

        PatchInfo(root);
        var components = EnsureObject(root, "components");
        PatchSecuritySchemes(components);
        PatchSchemas(components);
        PatchResponses(components);
        PatchPaths(root);
        PatchTags(root);
        root["x-taskforge-snapshot-version"] = "2.0";
        root["x-taskforge-agent-contract"] = "1.2";
        return root.ToJsonString(JsonOptions);
    }

    private static void PatchInfo(JsonObject root)
    {
        var info = EnsureObject(root, "info");
        info["title"] = "TaskForge.by Browser and Agent API";
        info["version"] = "1.2";
        info["description"] = "Machine-readable TaskForge.by contract for public semantic inspection, PNG/PDF Chromium renders, crawler captures, ordinary AI-account authentication helpers and controlled interactive sessions. Session-scoped endpoints require X-TaskForge-Browser-Session-Token; authenticated sessions additionally require the same ordinary TaskForge Bearer identity that created the session.";
    }

    private static void PatchSecuritySchemes(JsonObject components)
    {
        var schemes = EnsureObject(components, "securitySchemes");
        schemes["BrowserSessionToken"] = new JsonObject
        {
            ["type"] = "apiKey",
            ["in"] = "header",
            ["name"] = "X-TaskForge-Browser-Session-Token",
            ["description"] = "Random token returned by POST /api/browser/sessions. Required on every request for that session. Authenticated sessions also require the same TaskForge Bearer user identity."
        };
    }

    private static void PatchSchemas(JsonObject components)
    {
        var schemas = EnsureObject(components, "schemas");

        schemas["ApiError"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = Array("message", "code"),
            ["properties"] = new JsonObject
            {
                ["message"] = StringSchema("Human-readable Russian error message."),
                ["code"] = StringSchema("Stable machine-readable error code."),
                ["traceId"] = NullableStringSchema("Request trace identifier."),
                ["retryAfterSeconds"] = new JsonObject { ["type"] = "integer", ["nullable"] = true, ["minimum"] = 1 },
                ["details"] = new JsonObject { ["type"] = "object", ["nullable"] = true, ["additionalProperties"] = true, ["description"] = "Structured diagnostics such as capture stage, safe URL, readiness, pending requests or rate-limit reset." }
            }
        };

        schemas["ValidationProblemDetails"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = Array("title", "status", "errors"),
            ["additionalProperties"] = true,
            ["properties"] = new JsonObject
            {
                ["type"] = NullableStringSchema("Problem type URI."),
                ["title"] = StringSchema("Validation failure summary."),
                ["status"] = new JsonObject { ["type"] = "integer", ["enum"] = new JsonArray(400) },
                ["detail"] = NullableStringSchema("Optional validation detail."),
                ["instance"] = NullableStringSchema("Optional request path."),
                ["errors"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" }
                    }
                }
            }
        };

        schemas["IdentityError"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = Array("message"),
            ["additionalProperties"] = true,
            ["properties"] = new JsonObject
            {
                ["message"] = StringSchema("Human-readable identity error message."),
                ["code"] = NullableStringSchema("Optional stable identity error code."),
                ["severity"] = NullableStringSchema("Optional UI severity."),
                ["reason"] = NullableStringSchema("Optional account-block reason."),
                ["expiresAtUtc"] = NullableStringSchema("Optional account-block expiry.", format: "date-time")
            }
        };

        schemas["TaskForgeRegisterRequest"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = Array("login", "password", "accountType"),
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["login"] = new JsonObject { ["type"] = "string", ["minLength"] = 3, ["maxLength"] = 64, ["pattern"] = "^[a-zA-Z0-9_.-]+$" },
                ["email"] = NullableStringSchema("Optional email address.", format: "email", maxLength: 320),
                ["password"] = new JsonObject { ["type"] = "string", ["format"] = "password", ["minLength"] = 8, ["maxLength"] = 256 },
                ["firstName"] = NullableStringSchema("Optional display name.", maxLength: 120),
                ["lastName"] = NullableStringSchema("Optional family name.", maxLength: 120),
                ["phoneNumber"] = NullableStringSchema("Optional phone number.", maxLength: 40),
                ["additionalDataJson"] = NullableStringSchema("Optional JSON string with ordinary profile metadata."),
                ["accountType"] = new JsonObject { ["type"] = "string", ["enum"] = Array("ai"), ["description"] = "Self-declared AI marker; grants no elevated permission." }
            }
        };

        schemas["TaskForgeRegisterResponse"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = Array("message", "userId", "login", "role", "accountType", "isAi"),
            ["properties"] = new JsonObject
            {
                ["message"] = StringSchema("Registration result message."),
                ["userId"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
                ["login"] = new JsonObject { ["type"] = "string" },
                ["role"] = new JsonObject { ["type"] = "string" },
                ["accountType"] = new JsonObject { ["type"] = "string", ["enum"] = Array("ai") },
                ["isAi"] = new JsonObject { ["type"] = "boolean", ["enum"] = new JsonArray(true) }
            }
        };

        schemas["TaskForgeLoginRequest"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = Array("password"),
            ["additionalProperties"] = false,
            ["anyOf"] = new JsonArray(
                new JsonObject
                {
                    ["required"] = Array("login"),
                    ["properties"] = new JsonObject
                    {
                        ["login"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 64 }
                    }
                },
                new JsonObject
                {
                    ["required"] = Array("email"),
                    ["properties"] = new JsonObject
                    {
                        ["email"] = new JsonObject { ["type"] = "string", ["format"] = "email", ["minLength"] = 1, ["maxLength"] = 320 }
                    }
                }),
            ["properties"] = new JsonObject
            {
                ["login"] = NullableStringSchema("Login name. Either login or email is required.", maxLength: 64),
                ["email"] = NullableStringSchema("Email address. Either login or email is required.", format: "email", maxLength: 320),
                ["password"] = new JsonObject { ["type"] = "string", ["format"] = "password", ["minLength"] = 1, ["maxLength"] = 256 }
            }
        };

        schemas["TaskForgeLoginResponse"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = Array("accessToken", "user"),
            ["description"] = "Identity API response. Use accessToken as Authorization: Bearer <token> for authenticated Browser API calls. Refresh state is kept in the normal secure identity cookie and is not exposed to Browser API sessions.",
            ["properties"] = new JsonObject
            {
                ["accessToken"] = StringSchema("Ordinary TaskForge access token."),
                ["user"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = true }
            }
        };

        PatchRequired(schemas, "ClickBrowserSessionRequest", "elementId");
        PatchRequired(schemas, "FillBrowserSessionRequest", "elementId");
        PatchRequired(schemas, "PressBrowserSessionRequest", "elementId", "key");
        PatchRequired(schemas, "SelectBrowserSessionRequest", "elementId", "value");
        PatchRequired(schemas, "HoverBrowserSessionRequest", "elementId");
        PatchRequired(schemas, "CheckBrowserSessionRequest", "elementId");

        PatchProperty(schemas, "CreateBrowserSessionRequest", "site", new JsonObject
        {
            ["type"] = "string",
            ["nullable"] = true,
            ["enum"] = new JsonArray("main", "ct"),
            ["description"] = "Configured TaskForge frontend; defaults to main."
        });
        PatchProperty(schemas, "CreateBrowserSessionRequest", "path", RelativePathSchema());
        PatchProperty(schemas, "NavigateBrowserSessionRequest", "path", RelativePathSchema());
        foreach (var schemaName in new[]
                 {
                     "ClickBrowserSessionRequest", "FillBrowserSessionRequest", "PressBrowserSessionRequest",
                     "SelectBrowserSessionRequest", "HoverBrowserSessionRequest", "CheckBrowserSessionRequest",
                     "ScrollBrowserSessionRequest"
                 })
        {
            PatchProperty(schemas, schemaName, "elementId", ElementIdSchema(nullable: schemaName == "ScrollBrowserSessionRequest"));
        }

        PatchRequired(schemas, "SiteRouteDto", "site", "path", "title", "requiresAuthentication", "kind");
        PatchRequired(schemas, "SiteRoutesResponse", "routes");
        PatchRequired(schemas, "SiteViewportLimits", "minWidth", "maxWidth", "minHeight", "maxHeight");
        PatchRequired(schemas, "SiteApiLimits",
            "viewport", "maxFullPageHeight", "maxScreenshotPixels", "maxArtifactResponseBytes",
            "maxSnapshotElements", "maxSnapshotTextCharacters", "maxAriaSnapshotCharacters",
            "activeSessions", "anonymousSessionsPerOwner", "authenticatedSessionsPerOwner",
            "sessionIdleMinutes", "sessionAbsoluteMinutes", "agentArtifactTtlSeconds",
            "captureTimeoutSeconds", "captureCacheSeconds", "recommendedCaptureConcurrency",
            "semanticSnapshotVersion", "rateLimitHeaders");
        PatchRequired(schemas, "SiteInfoResponse",
            "name", "apiVersion", "defaultSite", "sites", "anonymousBrowsing", "aiAccounts",
            "interactiveBrowserSessions", "discovery", "instructions", "openApi", "registration",
            "login", "snapshot", "render", "browserSessions", "limits");
        PatchRequired(schemas, "BrowserSessionHeader", "name", "value");
        PatchRequired(schemas, "CreateBrowserSessionResponse",
            "id", "sessionToken", "site", "url", "readOnly", "authenticated", "accountType",
            "createdAtUtc", "expiresAtUtc", "width", "height", "snapshotUrl", "screenshotUrl",
            "sessionHeader", "snapshot");
        PatchRequired(schemas, "BrowserActionResponse", "sessionId", "action", "url", "title", "atUtc");
        PatchRequired(schemas, "SiteSnapshotResponse",
            "semanticSnapshotVersion", "site", "url", "title", "capturedAtUtc", "authenticated",
            "accountType", "readOnly", "captureMode", "policyInterference", "pageReadyState", "appReady",
            "readiness", "viewport", "document", "text", "ariaSnapshot", "headings", "elements",
            "discoveredLinks", "issues", "performance", "console", "networkFailures", "httpErrors",
            "policyBlockedRequests", "truncation");
    }

    private static void PatchResponses(JsonObject components)
    {
        var responses = EnsureObject(components, "responses");
        responses["BadRequest"] = OneOfResponse(
            "Invalid request, unsupported capture parameters or DataAnnotations validation failure.",
            "ApiError",
            "ValidationProblemDetails");
        responses["IdentityBadRequest"] = OneOfResponse(
            "Identity validation or request-binding failure.",
            "IdentityError",
            "ValidationProblemDetails");
        responses["IdentityUnauthorized"] = ResponseWithSchema(
            "The supplied login/email or password is invalid.",
            "IdentityError");
        responses["IdentityLocked"] = ResponseWithSchema(
            "The account is blocked, deleted or merged.",
            "IdentityError");
        responses["Unauthorized"] = ErrorResponse("Missing or invalid ordinary TaskForge authentication or session token.");
        responses["Forbidden"] = ErrorResponse("The ordinary TaskForge user is not allowed to perform the requested mutation.");
        responses["NotFound"] = ErrorResponse("Route, session, element or temporary artifact was not found.");
        responses["Gone"] = ErrorResponse("The browser session existed but has already been closed or expired.");
        responses["Conflict"] = ErrorResponse("The browser state changed, the page left the selected origin, or an element reference became stale.");
        responses["PayloadTooLarge"] = ErrorResponse("The generated visual artifact exceeds configured limits.");
        responses["InternalError"] = ErrorResponse("Unexpected Browser API failure. Use traceId when reporting the problem.");
        responses["BrowserFailure"] = ErrorResponse("Chromium rendering failed or timed out; error details include a safe stage/readiness diagnostic when available.");
        responses["ServiceUnavailable"] = new JsonObject
        {
            ["description"] = "Browser capacity or the temporary artifact store is unavailable. Respect Retry-After when present.",
            ["headers"] = new JsonObject
            {
                ["Retry-After"] = Header("integer", "Seconds to wait before retrying when the server supplied a retry budget.")
            },
            ["content"] = ErrorContent()
        };
        responses["TooManyRequests"] = new JsonObject
        {
            ["description"] = "Browser API rate limit exceeded. Respect Retry-After and avoid parallel render floods.",
            ["headers"] = BrowserRateLimitHeaders(includeRetryAfter: true),
            ["content"] = ErrorContent()
        };
        responses["IdentityTooManyRequests"] = new JsonObject
        {
            ["description"] = "Identity authentication limit exceeded. Respect Retry-After before retrying login or registration.",
            ["headers"] = IdentityRateLimitHeaders(includeRetryAfter: true),
            ["content"] = ErrorContent()
        };
    }

    private static void PatchPaths(JsonObject root)
    {
        var paths = EnsureObject(root, "paths");
        AddAuthenticationPaths(paths);

        foreach (var pathPair in paths.ToArray())
        {
            if (pathPair.Value is not JsonObject pathItem) continue;
            var browserRateLimited = IsBrowserRateLimitedPath(pathPair.Key);
            var authHelper = IsAuthenticationHelperPath(pathPair.Key);

            foreach (var methodPair in pathItem.ToArray())
            {
                if (methodPair.Key is not ("get" or "post" or "put" or "patch" or "delete" or "options" or "head")) continue;
                if (methodPair.Value is not JsonObject operation) continue;

                var responses = EnsureObject(operation, "responses");
                if (browserRateLimited)
                {
                    EnsureResponseRef(responses, "400", "BadRequest");
                    EnsureResponseRef(responses, "429", "TooManyRequests");
                    EnsureResponseRef(responses, "503", "ServiceUnavailable");
                    EnsureResponseRef(responses, "500", "InternalError");
                    PatchSuccessHeaders(responses, BrowserRateLimitHeaders(includeRetryAfter: false));
                }
                else if (!authHelper && pathPair.Key.StartsWith("/api/", StringComparison.Ordinal))
                {
                    EnsureResponseRef(responses, "500", "InternalError");
                }

                if (pathPair.Key.Equals("/api/browser/sessions", StringComparison.Ordinal))
                {
                    operation["security"] = OptionalBearerSecurity();
                    AppendDescription(operation, "Authorization: Bearer is optional for creation. Anonymous sessions are always read-only; readOnly=false requires an ordinary TaskForge access token.");
                }
                else if (pathPair.Key.StartsWith("/api/browser/sessions/{", StringComparison.Ordinal))
                {
                    operation["security"] = new JsonArray(
                        new JsonObject { ["BrowserSessionToken"] = new JsonArray() },
                        new JsonObject { ["BrowserSessionToken"] = new JsonArray(), ["Bearer"] = new JsonArray() });
                    AppendDescription(operation, "Requires X-TaskForge-Browser-Session-Token. Authenticated sessions additionally require the same ordinary TaskForge Bearer identity used at creation.");
                    EnsureResponseRef(responses, "401", "Unauthorized");
                    EnsureResponseRef(responses, "403", "Forbidden");
                    EnsureResponseRef(responses, "404", "NotFound");
                    EnsureResponseRef(responses, "410", "Gone");
                    EnsureResponseRef(responses, "409", "Conflict");
                }
                else if (pathPair.Key is "/api/site/snapshot" or "/api/site/render" or "/api/site/render.pdf" or "/api/site/info" or "/api/site/routes" or "/ai-artifacts/{id}/{fileName}")
                {
                    operation["security"] = OptionalBearerSecurity();
                }

                if (IsVisualArtifactPath(pathPair.Key))
                {
                    EnsureResponseRef(responses, "413", "PayloadTooLarge");
                }

                if (IsChromiumExecutionPath(pathPair.Key, methodPair.Key))
                {
                    EnsureResponseRef(responses, "502", "BrowserFailure");
                    EnsureResponseRef(responses, "504", "BrowserFailure");
                }

                if (pathPair.Key.StartsWith("/ai-artifacts/", StringComparison.Ordinal))
                {
                    EnsureResponseRef(responses, "404", "NotFound");
                }
            }
        }

        SetBinaryResponse(paths, "/api/site/render", "get", "image/png", "Authoritative Chromium PNG.");
        SetBinaryResponse(paths, "/api/site/render.pdf", "get", "application/pdf", "PDF compatibility wrapper around the authoritative PNG.");
        SetBinaryResponse(paths, "/api/browser/sessions/{id}/screenshot", "get", "image/png", "Current browser-session screenshot.");
        SetTextResponseByPrefix(paths, "/api/site/agent/capture/", "get", "text/html", "Crawler-friendly capture manifest with temporary snapshot/PNG/PDF links.");
        SetArtifactResponse(paths, "/ai-artifacts/{id}/{fileName}");

        SetJsonResponse(paths, "/api/site/info", "get", "200", "SiteInfoResponse", "Browser API capabilities and limits.");
        SetJsonResponse(paths, "/api/site/routes", "get", "200", "SiteRoutesResponse", "Known main and CT frontend routes.");
        SetJsonResponse(paths, "/api/site/snapshot", "get", "200", "SiteSnapshotResponse", "Semantic snapshot of the rendered TaskForge page.");
        SetJsonResponse(paths, "/api/browser/sessions", "post", "201", "CreateBrowserSessionResponse", "Browser session created.", removeStatus: "200");
        AddResponseHeader(
            paths,
            "/api/browser/sessions",
            "post",
            "201",
            "X-TaskForge-Browser-Session-Token",
            Header("string", "Session-scoped credential. Send it on every request to the created session."));
        SetJsonResponse(paths, "/api/browser/sessions/{id}/snapshot", "get", "200", "SiteSnapshotResponse", "Current semantic session snapshot.");

        foreach (var action in new[] { "navigate", "click", "fill", "press", "select", "hover", "check", "scroll", "back", "reload" })
        {
            SetJsonResponse(paths, $"/api/browser/sessions/{{id}}/{action}", "post", "200", "BrowserActionResponse", $"Browser session {action} action result.");
        }

        SetNoContentResponse(paths, "/api/browser/sessions/{id}", "delete", "Browser session closed.");
        PatchAllBrowserSuccessHeaders(paths);

        SetExample(paths, "/api/browser/sessions", "post", new JsonObject
        {
            ["site"] = "main",
            ["path"] = "/courses",
            ["width"] = 390,
            ["height"] = 844,
            ["readOnly"] = true,
            ["waitMs"] = 800
        });
        SetExample(paths, "/api/browser/sessions/{id}/navigate", "post", new JsonObject { ["path"] = "/news", ["waitMs"] = 800 });
        SetExample(paths, "/api/browser/sessions/{id}/click", "post", new JsonObject { ["elementId"] = "tf3", ["includeSnapshot"] = true });
        SetExample(paths, "/api/browser/sessions/{id}/fill", "post", new JsonObject { ["elementId"] = "tf2", ["value"] = "my-agent-name", ["includeSnapshot"] = true });
        SetExample(paths, "/api/browser/sessions/{id}/press", "post", new JsonObject { ["elementId"] = "tf2", ["key"] = "Enter", ["includeSnapshot"] = true });
        SetExample(paths, "/api/browser/sessions/{id}/select", "post", new JsonObject { ["elementId"] = "tf4", ["value"] = "option-value", ["includeSnapshot"] = true });
        SetExample(paths, "/api/browser/sessions/{id}/hover", "post", new JsonObject { ["elementId"] = "tf3", ["includeSnapshot"] = false });
        SetExample(paths, "/api/browser/sessions/{id}/check", "post", new JsonObject { ["elementId"] = "tf5", ["checked"] = true, ["includeSnapshot"] = true });
        SetExample(paths, "/api/browser/sessions/{id}/scroll", "post", new JsonObject { ["deltaY"] = 600, ["includeSnapshot"] = true });
    }

    private static void AddAuthenticationPaths(JsonObject paths)
    {
        var registerResponses = new JsonObject
        {
            ["200"] = ResponseWithSchema("Account registered.", "TaskForgeRegisterResponse"),
            ["400"] = RefResponse("IdentityBadRequest"),
            ["429"] = RefResponse("IdentityTooManyRequests"),
            ["500"] = RefResponse("InternalError")
        };
        PatchSuccessHeaders(registerResponses, IdentityRateLimitHeaders(includeRetryAfter: false));

        paths["/api/auth/register"] = new JsonObject
        {
            ["post"] = new JsonObject
            {
                ["tags"] = Array("Authentication helpers"),
                ["summary"] = "Register an ordinary AI-marked TaskForge.by account",
                ["description"] = "Gateway-proxied identity endpoint documented here for agent onboarding. accountType=ai is self-declared and grants no elevated permission.",
                ["operationId"] = "RegisterTaskForgeAiAccount",
                ["requestBody"] = JsonRequestBody("TaskForgeRegisterRequest", new JsonObject
                {
                    ["login"] = "my-agent-name",
                    ["email"] = null,
                    ["password"] = "use-a-strong-unique-password",
                    ["firstName"] = "My agent",
                    ["lastName"] = "",
                    ["phoneNumber"] = null,
                    ["additionalDataJson"] = null,
                    ["accountType"] = "ai"
                }),
                ["responses"] = registerResponses
            }
        };

        var loginResponses = new JsonObject
        {
            ["200"] = ResponseWithSchema("Authenticated.", "TaskForgeLoginResponse"),
            ["400"] = RefResponse("IdentityBadRequest"),
            ["401"] = RefResponse("IdentityUnauthorized"),
            ["423"] = RefResponse("IdentityLocked"),
            ["429"] = RefResponse("IdentityTooManyRequests"),
            ["500"] = RefResponse("InternalError")
        };
        PatchSuccessHeaders(loginResponses, IdentityRateLimitHeaders(includeRetryAfter: false));

        paths["/api/auth/login"] = new JsonObject
        {
            ["post"] = new JsonObject
            {
                ["tags"] = Array("Authentication helpers"),
                ["summary"] = "Log in with an ordinary TaskForge.by account",
                ["description"] = "Gateway-proxied identity endpoint. Use the returned accessToken in Authorization: Bearer for authenticated Browser API calls.",
                ["operationId"] = "LoginTaskForgeAccount",
                ["requestBody"] = JsonRequestBody("TaskForgeLoginRequest", new JsonObject
                {
                    ["login"] = "my-agent-name",
                    ["email"] = null,
                    ["password"] = "use-a-strong-unique-password"
                }),
                ["responses"] = loginResponses
            }
        };
    }

    private static void PatchTags(JsonObject root)
    {
        root["tags"] = new JsonArray
        {
            new JsonObject { ["name"] = "Discovery", ["description"] = "Machine-readable entry points and instructions." },
            new JsonObject { ["name"] = "Authentication helpers", ["description"] = "Identity endpoints documented for ordinary AI-account onboarding." },
            new JsonObject { ["name"] = "Site inspection", ["description"] = "Stateless semantic and visual inspection." },
            new JsonObject { ["name"] = "Agent access", ["description"] = "Crawler-friendly short-lived public capture artifacts." },
            new JsonObject { ["name"] = "Interactive browser", ["description"] = "Controlled Chromium sessions addressed by tfN element references." }
        };
    }

    private static void PatchRequired(JsonObject schemas, string schemaName, params string[] required)
    {
        if (schemas[schemaName] is not JsonObject schema) return;
        schema["required"] = new JsonArray(required.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    }

    private static void PatchProperty(JsonObject schemas, string schemaName, string propertyName, JsonObject replacement)
    {
        if (schemas[schemaName] is not JsonObject schema) return;
        var properties = EnsureObject(schema, "properties");
        properties[propertyName] = replacement;
    }

    private static JsonObject RelativePathSchema() => new()
    {
        ["type"] = "string",
        ["nullable"] = true,
        ["maxLength"] = 2048,
        ["pattern"] = "^/[^\\r\\n]*$",
        ["description"] = "Relative path on the selected TaskForge.by origin. Arbitrary absolute URLs are rejected."
    };

    private static JsonObject ElementIdSchema(bool nullable) => new()
    {
        ["type"] = "string",
        ["nullable"] = nullable,
        ["pattern"] = "^tf[1-9][0-9]{0,5}$",
        ["description"] = "Element reference from the latest semantic snapshot. Refresh after navigation or major DOM changes."
    };

    private static void PatchSuccessHeaders(JsonObject responses, JsonObject rateHeaders)
    {
        foreach (var status in new[] { "200", "201", "202", "204" })
        {
            if (responses[status] is not JsonObject success || success["$ref"] is not null) continue;
            var headers = EnsureObject(success, "headers");
            foreach (var pair in rateHeaders) headers[pair.Key] = pair.Value?.DeepClone();
        }
    }

    private static JsonObject BrowserRateLimitHeaders(bool includeRetryAfter)
    {
        var headers = new JsonObject
        {
            ["RateLimit-Limit"] = Header("integer", "Maximum requests in the active window."),
            ["RateLimit-Remaining"] = Header("integer", "Requests remaining in the active window."),
            ["RateLimit-Reset"] = Header("integer", "Seconds until the active window resets."),
            ["RateLimit-Policy"] = Header("string", "Applied fixed-window policy, for example 10;w=60."),
            ["X-RateLimit-Limit"] = Header("integer", "Compatibility copy of RateLimit-Limit."),
            ["X-RateLimit-Remaining"] = Header("integer", "Compatibility copy of RateLimit-Remaining."),
            ["X-RateLimit-Reset"] = Header("integer", "UTC Unix timestamp when the active window resets.")
        };
        if (includeRetryAfter) headers["Retry-After"] = Header("integer", "Seconds to wait before retrying.");
        return headers;
    }

    private static JsonObject IdentityRateLimitHeaders(bool includeRetryAfter)
    {
        var headers = new JsonObject
        {
            ["X-RateLimit-Limit"] = Header("integer", "Maximum identity attempts in the active window."),
            ["X-RateLimit-Remaining"] = Header("integer", "Identity attempts remaining in the active window.")
        };
        if (includeRetryAfter) headers["Retry-After"] = Header("integer", "Seconds to wait before retrying.");
        return headers;
    }

    private static JsonObject Header(string type, string description) => new()
    {
        ["description"] = description,
        ["schema"] = new JsonObject { ["type"] = type }
    };

    private static void EnsureResponseRef(JsonObject responses, string status, string component)
    {
        if (responses[status] is null) responses[status] = RefResponse(component);
    }

    private static JsonObject RefResponse(string component) => new() { ["$ref"] = $"#/components/responses/{component}" };
    private static JsonObject SchemaRef(string schema) => new() { ["$ref"] = $"#/components/schemas/{schema}" };

    private static JsonObject ErrorResponse(string description) => new()
    {
        ["description"] = description,
        ["content"] = ErrorContent()
    };

    private static JsonObject ErrorContent() => new()
    {
        ["application/json"] = new JsonObject { ["schema"] = SchemaRef("ApiError") }
    };

    private static JsonObject ResponseWithSchema(string description, string schema) => new()
    {
        ["description"] = description,
        ["content"] = new JsonObject
        {
            ["application/json"] = new JsonObject { ["schema"] = SchemaRef(schema) }
        }
    };

    private static JsonObject OneOfResponse(string description, params string[] schemas) => new()
    {
        ["description"] = description,
        ["content"] = new JsonObject
        {
            ["application/json"] = new JsonObject
            {
                ["schema"] = new JsonObject
                {
                    ["oneOf"] = new JsonArray(schemas.Select(schema => (JsonNode?)SchemaRef(schema)).ToArray())
                }
            }
        }
    };

    private static JsonObject JsonRequestBody(string schema, JsonObject example) => new()
    {
        ["required"] = true,
        ["content"] = new JsonObject
        {
            ["application/json"] = new JsonObject
            {
                ["schema"] = SchemaRef(schema),
                ["example"] = example
            }
        }
    };

    private static void SetExample(JsonObject paths, string path, string method, JsonObject example)
    {
        var operation = FindOperation(paths, path, method);
        if (operation is null) return;
        var body = operation["requestBody"] as JsonObject;
        var content = body?["content"] as JsonObject;
        var json = content?["application/json"] as JsonObject;
        if (json is not null) json["example"] = example;
    }

    private static void SetBinaryResponse(JsonObject paths, string path, string method, string contentType, string description)
    {
        var operation = FindOperation(paths, path, method);
        if (operation is null) return;
        var responses = EnsureObject(operation, "responses");
        var success = responses["200"] as JsonObject ?? new JsonObject();
        success["description"] = description;
        success["content"] = new JsonObject
        {
            [contentType] = new JsonObject
            {
                ["schema"] = new JsonObject { ["type"] = "string", ["format"] = "binary" }
            }
        };
        responses["200"] = success;
    }

    private static void SetJsonResponse(
        JsonObject paths,
        string path,
        string method,
        string status,
        string schema,
        string description,
        string? removeStatus = null)
    {
        var operation = FindOperation(paths, path, method);
        if (operation is null) return;
        var responses = EnsureObject(operation, "responses");
        if (!string.IsNullOrWhiteSpace(removeStatus)) responses.Remove(removeStatus);

        var success = responses[status] as JsonObject ?? new JsonObject();
        success["description"] = description;
        success["content"] = new JsonObject
        {
            ["application/json"] = new JsonObject { ["schema"] = SchemaRef(schema) }
        };
        responses[status] = success;
    }

    private static void SetNoContentResponse(JsonObject paths, string path, string method, string description)
    {
        var operation = FindOperation(paths, path, method);
        if (operation is null) return;
        var responses = EnsureObject(operation, "responses");
        responses.Remove("200");
        responses["204"] = new JsonObject { ["description"] = description };
    }

    private static void AddResponseHeader(
        JsonObject paths,
        string path,
        string method,
        string status,
        string name,
        JsonObject header)
    {
        var operation = FindOperation(paths, path, method);
        var responses = operation?["responses"] as JsonObject;
        var response = responses?[status] as JsonObject;
        if (response is null) return;
        EnsureObject(response, "headers")[name] = header;
    }

    private static void SetArtifactResponse(JsonObject paths, string path)
    {
        var operation = FindOperation(paths, path, "get");
        if (operation is null) return;
        var responses = EnsureObject(operation, "responses");
        var success = responses["200"] as JsonObject ?? new JsonObject();
        success["description"] = "Short-lived immutable artifact. The fileName selects semantic snapshot JSON, authoritative PNG or optional PDF.";
        success["content"] = new JsonObject
        {
            ["application/json"] = new JsonObject { ["schema"] = SchemaRef("SiteSnapshotResponse") },
            ["image/png"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "string", ["format"] = "binary" } },
            ["application/pdf"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "string", ["format"] = "binary" } }
        };
        responses["200"] = success;
    }

    private static void PatchAllBrowserSuccessHeaders(JsonObject paths)
    {
        var rateHeaders = BrowserRateLimitHeaders(includeRetryAfter: false);
        foreach (var pathPair in paths)
        {
            if (!IsBrowserRateLimitedPath(pathPair.Key) || pathPair.Value is not JsonObject pathItem) continue;
            foreach (var methodPair in pathItem)
            {
                if (methodPair.Key is not ("get" or "post" or "put" or "patch" or "delete")
                    || methodPair.Value is not JsonObject operation) continue;
                PatchSuccessHeaders(EnsureObject(operation, "responses"), rateHeaders);
            }
        }
    }

    private static void SetTextResponseByPrefix(JsonObject paths, string pathPrefix, string method, string contentType, string description)
    {
        var path = paths.FirstOrDefault(pair => pair.Key.StartsWith(pathPrefix, StringComparison.Ordinal)).Key;
        if (string.IsNullOrWhiteSpace(path)) return;
        var operation = FindOperation(paths, path, method);
        if (operation is null) return;
        var responses = EnsureObject(operation, "responses");
        var success = responses["200"] as JsonObject ?? new JsonObject();
        success["description"] = description;
        success["content"] = new JsonObject
        {
            [contentType] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "string" } }
        };
        responses["200"] = success;
    }

    private static JsonObject? FindOperation(JsonObject paths, string canonicalPath, string method)
    {
        if (paths[canonicalPath] is JsonObject exact && exact[method] is JsonObject exactOperation) return exactOperation;
        var canonicalSegments = NormalizePathTemplate(canonicalPath);
        foreach (var pair in paths)
        {
            if (NormalizePathTemplate(pair.Key) != canonicalSegments || pair.Value is not JsonObject pathItem) continue;
            if (pathItem[method] is JsonObject operation) return operation;
        }
        return null;
    }

    private static string NormalizePathTemplate(string path)
        => System.Text.RegularExpressions.Regex.Replace(path, "\\{[^}]+\\}", "{}");

    private static bool IsBrowserRateLimitedPath(string path)
        => path.StartsWith("/api/site/", StringComparison.Ordinal)
           || path.StartsWith("/api/browser/sessions", StringComparison.Ordinal)
           || path.StartsWith("/ai-artifacts/", StringComparison.Ordinal);

    private static bool IsVisualArtifactPath(string path)
        => path is "/api/site/render" or "/api/site/render.pdf"
           || path.Contains("/screenshot", StringComparison.Ordinal)
           || path.StartsWith("/api/site/agent/capture/", StringComparison.Ordinal);

    private static bool IsChromiumExecutionPath(string path, string method)
        => path is "/api/site/snapshot" or "/api/site/render" or "/api/site/render.pdf"
           || path.StartsWith("/api/site/agent/capture/", StringComparison.Ordinal)
           || (path == "/api/browser/sessions" && method == "post")
           || (path.StartsWith("/api/browser/sessions/", StringComparison.Ordinal) && method != "delete");

    private static bool IsAuthenticationHelperPath(string path)
        => path is "/api/auth/register" or "/api/auth/login";

    private static JsonArray OptionalBearerSecurity()
        => new(new JsonObject(), new JsonObject { ["Bearer"] = new JsonArray() });

    private static void AppendDescription(JsonObject operation, string value)
    {
        var current = operation["description"]?.GetValue<string>();
        operation["description"] = string.IsNullOrWhiteSpace(current) ? value : current + "\n\n" + value;
    }

    private static JsonObject StringSchema(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject NullableStringSchema(string description, string? format = null, int? maxLength = null)
    {
        var schema = new JsonObject { ["type"] = "string", ["nullable"] = true, ["description"] = description };
        if (!string.IsNullOrWhiteSpace(format)) schema["format"] = format;
        if (maxLength is > 0) schema["maxLength"] = maxLength.Value;
        return schema;
    }

    private static JsonArray Array(params string[] values)
        => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static JsonObject EnsureObject(JsonObject parent, string property)
    {
        if (parent[property] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[property] = created;
        return created;
    }
}
