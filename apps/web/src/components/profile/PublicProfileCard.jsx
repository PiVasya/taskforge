import React from "react";
import { Card, Badge } from "../ui";
import { Github, Send, Globe2, MapPin, BookOpen, Trophy, AtSign } from "lucide-react";

function safeLink(value) {
  const raw = String(value || "").trim();
  if (!raw) return "";
  if (/^https?:\/\//i.test(raw)) return raw;
  return `https://${raw}`;
}

function telegramLink(value) {
  const raw = String(value || "").trim();
  if (!raw) return "";
  if (/^https?:\/\//i.test(raw)) return raw;
  return `https://t.me/${raw.replace(/^@/, "")}`;
}

function profileLogin(profile) {
  return String(profile?.login || profile?.username || "").trim();
}

function initials(profile) {
  const name = String(
    profile?.displayName ||
      `${profile?.lastName || ""} ${profile?.firstName || ""}` ||
      profileLogin(profile) ||
      "",
  ).trim();
  if (!name) return "TF";
  const parts = name.split(/\s+/).filter(Boolean);
  const value = parts.length > 1 ? `${parts[0][0]}${parts[1][0]}` : parts[0][0];
  return value.toUpperCase();
}

export default function PublicProfileCard({
  profile,
  badges = [],
  badgesLoading = false,
  embedded = false,
}) {
  const login = profileLogin(profile);
  const fullName = [profile?.lastName, profile?.firstName].filter(Boolean).join(" ").trim();
  const displayName = profile?.displayName || fullName || login || "Пользователь";
  const avatarUrl = profile?.avatarUrl || profile?.profilePictureUrl || "";
  const github = safeLink(profile?.github);
  const telegram = telegramLink(profile?.telegram);
  const website = safeLink(profile?.website);
  const skills = Array.isArray(profile?.skills)
    ? profile.skills.filter(Boolean)
    : [];

  return (
    <div className={embedded ? "space-y-4" : "max-w-3xl mx-auto space-y-6"}>
      <Card className="p-6 flex gap-4">
        <div className="shrink-0">
          {avatarUrl ? (
            <img
              src={avatarUrl}
              alt={displayName}
              className="h-20 w-20 rounded-3xl object-cover border border-neutral-300/60 dark:border-neutral-700/60"
            />
          ) : (
            <div className="h-20 w-20 rounded-3xl bg-[rgba(var(--accent)/0.18)] grid place-items-center text-[rgb(var(--accent))] text-3xl font-semibold">
              {initials({ ...profile, displayName })}
            </div>
          )}
        </div>

        <div className="flex-1 space-y-2 min-w-0">
          <div className="flex items-center gap-3 flex-wrap">
            <div className="min-w-0">
              <h1 className="text-2xl font-semibold truncate">{displayName}</h1>
              {login ? (
                <div className="mt-1 inline-flex items-center gap-1 text-sm text-neutral-500 dark:text-neutral-400">
                  <AtSign size={14} />
                  <span className="truncate">{login}</span>
                </div>
              ) : null}
            </div>
            {typeof profile?.rank === "number" && (
              <div className="inline-flex items-center gap-1 rounded-full border border-[rgba(var(--border)/0.65)] px-3 py-1 text-xs font-medium text-neutral-500 dark:text-neutral-400">
                <Trophy size={14} />
                <span>#{profile.rank} в топе</span>
              </div>
            )}
          </div>

          {profile?.location && (
            <div className="flex items-center gap-1 text-sm text-neutral-500 dark:text-neutral-400">
              <MapPin size={14} />
              <span>{profile.location}</span>
            </div>
          )}

          {profile?.education && (
            <div className="flex items-center gap-1 text-sm text-neutral-500 dark:text-neutral-400">
              <BookOpen size={14} />
              <span>{profile.education}</span>
            </div>
          )}

          {profile?.bio ? (
            <p className="text-sm text-neutral-600 dark:text-neutral-300 mt-2 whitespace-pre-line">
              {profile.bio}
            </p>
          ) : (
            <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-2">
              Описание ещё не заполнено.
            </p>
          )}

          {badges.length > 0 && (
            <div className="flex flex-wrap items-center gap-1 mt-2">
              {badges.map((b) => (
                <span
                  key={b.id || b.name}
                  title={b.name}
                  className="inline-flex items-center justify-center"
                >
                  {b.imageUrl ? (
                    <img
                      src={b.imageUrl}
                      alt={b.name}
                      className="h-6 w-6 object-contain rounded border border-neutral-200 dark:border-neutral-700"
                    />
                  ) : null}
                </span>
              ))}
            </div>
          )}
        </div>
      </Card>

      <div className="grid gap-4 md:grid-cols-2">
        <Card className="p-4 space-y-2">
          <h2 className="font-semibold text-sm">Статистика</h2>
          <div className="text-sm space-y-1">
            <div>
              <span className="font-semibold">
                {profile?.score ?? profile?.rating ?? profile?.totalScore ?? 0}
              </span>{" "}
              рейтинга
            </div>
            <div>
              <span className="font-semibold">
                {profile?.solvedAssignments ?? 0}
              </span>{" "}
              решённых заданий
            </div>
            <div>
              <span className="font-semibold">
                {profile?.totalAttempts ?? 0}
              </span>{" "}
              попыток отправки решений
            </div>
          </div>
        </Card>

        <Card className="p-4 space-y-2">
          <h2 className="font-semibold text-sm">Ссылки</h2>
          <div className="flex flex-col gap-2 text-sm">
            {github ? (
              <a
                href={github}
                target="_blank"
                rel="noreferrer"
                className="inline-flex items-center gap-2 text-neutral-600 dark:text-neutral-300 hover:text-[rgb(var(--accent))]"
              >
                <Github size={16} />
                <span>GitHub</span>
              </a>
            ) : null}
            {telegram ? (
              <a
                href={telegram}
                target="_blank"
                rel="noreferrer"
                className="inline-flex items-center gap-2 text-neutral-600 dark:text-neutral-300 hover:text-[rgb(var(--accent))]"
              >
                <Send size={16} />
                <span>Telegram</span>
              </a>
            ) : null}
            {website ? (
              <a
                href={website}
                target="_blank"
                rel="noreferrer"
                className="inline-flex items-center gap-2 text-neutral-600 dark:text-neutral-300 hover:text-[rgb(var(--accent))]"
              >
                <Globe2 size={16} />
                <span>Сайт / портфолио</span>
              </a>
            ) : null}
            {!github && !telegram && !website ? (
              <div className="text-xs text-neutral-400">
                Пользователь не добавил ссылки.
              </div>
            ) : null}
          </div>
        </Card>
      </div>

      {skills.length > 0 ? (
        <Card className="p-4 space-y-2">
          <h2 className="font-semibold text-sm">Навыки</h2>
          <div className="flex flex-wrap gap-2">
            {skills.map((skill) => (
              <Badge key={skill}>{skill}</Badge>
            ))}
          </div>
        </Card>
      ) : null}

      {badgesLoading || badges.length > 0 ? (
        <Card className="p-4 space-y-2">
          <h2 className="font-semibold text-sm">Бейджи</h2>
          {badgesLoading ? (
            <div className="text-xs text-neutral-400">Загрузка…</div>
          ) : null}
          {!badgesLoading && badges.length > 0 ? (
            <div className="flex flex-wrap gap-3">
              {badges.map((badge) => (
                <div
                  key={badge.id || badge.name}
                  className="flex items-center gap-2 rounded-2xl border border-[rgba(var(--border)/0.65)] px-3 py-2"
                >
                  {badge.imageUrl ? (
                    <img
                      src={badge.imageUrl}
                      alt={badge.name}
                      className="h-8 w-8 object-contain rounded"
                    />
                  ) : null}
                  <div className="text-sm font-medium">{badge.name}</div>
                </div>
              ))}
            </div>
          ) : null}
        </Card>
      ) : null}
    </div>
  );
}
