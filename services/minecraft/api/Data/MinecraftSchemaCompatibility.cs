using Microsoft.EntityFrameworkCore;

namespace TaskForge.Minecraft.Api.Data;

internal static class MinecraftSchemaCompatibility
{
    public static async Task EnsureMinecraftCompatibilitySchemaAsync(this MinecraftDbContext db, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS "MinecraftEconomySettings" (
    "Id" uuid NOT NULL,
    "WeeklyPenalty" integer NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_MinecraftEconomySettings" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS "MinecraftLinkCodes" (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "Nick" character varying(32) NOT NULL,
    "CodeHash" bytea NOT NULL,
    "Salt" bytea NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UsedAtUtc" timestamp with time zone NULL,
    CONSTRAINT "PK_MinecraftLinkCodes" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS "MinecraftWeeklyJoins" (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "WeekStartUtc" timestamp with time zone NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "PenaltyApplied" integer NOT NULL,
    CONSTRAINT "PK_MinecraftWeeklyJoins" PRIMARY KEY ("Id")
);

CREATE TABLE IF NOT EXISTS "MinecraftRatingTransactions" (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "PlayerName" character varying(120) NULL,
    "PlayerUuid" character varying(80) NULL,
    "Delta" integer NOT NULL,
    "Kind" character varying(64) NOT NULL,
    "Reason" character varying(500) NOT NULL,
    "RequestId" character varying(120) NULL,
    "MetadataJson" jsonb NULL,
    "ActorUserId" uuid NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_MinecraftRatingTransactions" PRIMARY KEY ("Id")
);

ALTER TABLE "MinecraftLinks" ADD COLUMN IF NOT EXISTS "ConfirmedAtUtc" timestamp with time zone NULL;
ALTER TABLE "MinecraftLinks" ADD COLUMN IF NOT EXISTS "UnlinkedAtUtc" timestamp with time zone NULL;
ALTER TABLE "MinecraftLinks" ALTER COLUMN "Code" TYPE character varying(80);

ALTER TABLE "MinecraftChatMessages" ADD COLUMN IF NOT EXISTS "UserId" uuid NULL;
ALTER TABLE "MinecraftChatMessages" ADD COLUMN IF NOT EXISTS "Source" character varying(64) NOT NULL DEFAULT 'SiteUser';
ALTER TABLE "MinecraftChatMessages" ADD COLUMN IF NOT EXISTS "AuthorName" character varying(120) NULL;
ALTER TABLE "MinecraftChatMessages" ADD COLUMN IF NOT EXISTS "MinecraftNick" character varying(32) NULL;
ALTER TABLE "MinecraftChatMessages" ADD COLUMN IF NOT EXISTS "MinecraftUuid" character varying(80) NULL;
ALTER TABLE "MinecraftChatMessages" ADD COLUMN IF NOT EXISTS "Message" character varying(2000) NOT NULL DEFAULT '';
ALTER TABLE "MinecraftChatMessages" ADD COLUMN IF NOT EXISTS "CreatedAtUtc" timestamp with time zone NOT NULL DEFAULT now();

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MinecraftChatMessages' AND column_name = 'Text') THEN
        UPDATE "MinecraftChatMessages" SET "Message" = COALESCE("Text", '') WHERE "Message" = '';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MinecraftChatMessages' AND column_name = 'Author') THEN
        UPDATE "MinecraftChatMessages" SET "AuthorName" = COALESCE("AuthorName", "Author") WHERE "AuthorName" IS NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MinecraftChatMessages' AND column_name = 'CreatedAt') THEN
        UPDATE "MinecraftChatMessages" SET "CreatedAtUtc" = "CreatedAt" WHERE "CreatedAtUtc" IS NULL;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS "IX_MinecraftEconomySettings_UpdatedAtUtc" ON "MinecraftEconomySettings" ("UpdatedAtUtc");
CREATE INDEX IF NOT EXISTS "IX_MinecraftLinkCodes_UserId_ExpiresAtUtc_UsedAtUtc" ON "MinecraftLinkCodes" ("UserId", "ExpiresAtUtc", "UsedAtUtc");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_MinecraftWeeklyJoins_UserId_WeekStartUtc" ON "MinecraftWeeklyJoins" ("UserId", "WeekStartUtc");
CREATE INDEX IF NOT EXISTS "IX_MinecraftRatingTransactions_UserId_CreatedAtUtc" ON "MinecraftRatingTransactions" ("UserId", "CreatedAtUtc");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_MinecraftRatingTransactions_RequestId" ON "MinecraftRatingTransactions" ("RequestId") WHERE "RequestId" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_MinecraftLinks_UserId_Confirmed_UnlinkedAtUtc" ON "MinecraftLinks" ("UserId", "Confirmed", "UnlinkedAtUtc");
CREATE INDEX IF NOT EXISTS "IX_MinecraftChatMessages_CreatedAtUtc" ON "MinecraftChatMessages" ("CreatedAtUtc");
""", ct);
    }
}
