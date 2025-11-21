// Утилита для работы с AdditionalDataJson

export function parseProfileExtra(json) {
  if (!json) {
    return {
      bio: '',
      location: '',
      education: '',
      github: '',
      telegram: '',
      website: '',
      skillsText: '',
      showInLeaderboard: true,
    };
  }

  let obj;
  try {
    obj = JSON.parse(json);
  } catch {
    // Если вдруг сломан JSON — не роняем страницу
    return {
      bio: '',
      location: '',
      education: '',
      github: '',
      telegram: '',
      website: '',
      skillsText: '',
      showInLeaderboard: true,
    };
  }

  const links = obj.links || {};
  const skills = Array.isArray(obj.skills) ? obj.skills : [];

  return {
    bio: obj.bio || '',
    location: obj.location || '',
    education: obj.education || '',
    github: links.github || '',
    telegram: links.telegram || '',
    website: links.website || '',
    // для формы удобно хранить навыки строкой "C#, C++, SQL"
    skillsText: skills.join(', '),
    showInLeaderboard:
      typeof obj.showInLeaderboard === 'boolean'
        ? obj.showInLeaderboard
        : true,
  };
}

export function buildProfileExtra(fields) {
  const skills =
    fields.skillsText
      ?.split(',')
      .map((s) => s.trim())
      .filter(Boolean) || [];

  const obj = {
    bio: fields.bio?.trim() || '',
    location: fields.location?.trim() || '',
    education: fields.education?.trim() || '',
    links: {
      github: fields.github?.trim() || null,
      telegram: fields.telegram?.trim() || null,
      website: fields.website?.trim() || null,
    },
    skills,
    showInLeaderboard: !!fields.showInLeaderboard,
  };

  return JSON.stringify(obj);
}
