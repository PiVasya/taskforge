const DEFAULT_PROFILE_EXTRA = Object.freeze({
  publicProfileEnabled: true,
  bio: '',
  location: '',
  education: '',
  github: '',
  telegram: '',
  website: '',
  skillsText: '',
  showInLeaderboard: true,
  showBio: false,
  showLocation: false,
  showEducation: false,
  showGithub: false,
  showTelegram: false,
  showWebsite: false,
  showSkills: false,
  showStats: true,
});

function defaults() {
  return { ...DEFAULT_PROFILE_EXTRA };
}

function readBool(obj, key, fallback) {
  return typeof obj?.[key] === 'boolean' ? obj[key] : fallback;
}

export function parseProfileExtra(json) {
  if (!json) return defaults();

  let obj;
  try {
    obj = JSON.parse(json);
  } catch {
    return defaults();
  }

  const links = obj?.links && typeof obj.links === 'object' ? obj.links : {};
  const skills = Array.isArray(obj?.skills) ? obj.skills : [];

  return {
    publicProfileEnabled: readBool(obj, 'publicProfileEnabled', true),
    bio: obj?.bio || '',
    location: obj?.location || '',
    education: obj?.education || '',
    github: links.github || '',
    telegram: links.telegram || '',
    website: links.website || '',
    skillsText: skills.filter(Boolean).join(', '),
    showInLeaderboard: readBool(obj, 'showInLeaderboard', true),
    showBio: readBool(obj, 'showBio', false),
    showLocation: readBool(obj, 'showLocation', false),
    showEducation: readBool(obj, 'showEducation', false),
    showGithub: readBool(obj, 'showGithub', false),
    showTelegram: readBool(obj, 'showTelegram', false),
    showWebsite: readBool(obj, 'showWebsite', false),
    showSkills: readBool(obj, 'showSkills', false),
    showStats: readBool(obj, 'showStats', true),
  };
}

export function buildProfileExtra(fields) {
  const skills =
    fields.skillsText
      ?.split(',')
      .map((s) => s.trim())
      .filter(Boolean) || [];

  const obj = {
    publicProfileEnabled: fields.publicProfileEnabled !== false,
    bio: fields.bio?.trim() || '',
    location: fields.location?.trim() || '',
    education: fields.education?.trim() || '',
    links: {
      github: fields.github?.trim() || null,
      telegram: fields.telegram?.trim() || null,
      website: fields.website?.trim() || null,
    },
    skills,
    showInLeaderboard: fields.showInLeaderboard !== false,
    showBio: fields.showBio === true,
    showLocation: fields.showLocation === true,
    showEducation: fields.showEducation === true,
    showGithub: fields.showGithub === true,
    showTelegram: fields.showTelegram === true,
    showWebsite: fields.showWebsite === true,
    showSkills: fields.showSkills === true,
    showStats: fields.showStats !== false,
  };

  return JSON.stringify(obj);
}
