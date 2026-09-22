import { buildProfileExtra, parseProfileExtra } from './profileExtra';

describe('profileExtra visibility', () => {
  test('legacy profile data keeps public stats enabled without exposing optional personal fields', () => {
    const parsed = parseProfileExtra(JSON.stringify({
      bio: 'О себе',
      location: 'Минск',
      links: { github: 'https://github.com/example' },
      skills: ['C#'],
    }));

    expect(parsed.publicProfileEnabled).toBe(true);
    expect(parsed.showStats).toBe(true);
    expect(parsed.showBio).toBe(false);
    expect(parsed.showLocation).toBe(false);
    expect(parsed.showGithub).toBe(false);
    expect(parsed.showSkills).toBe(false);
  });

  test('build and parse preserve every public visibility switch', () => {
    const encoded = buildProfileExtra({
      publicProfileEnabled: false,
      bio: 'Bio',
      location: 'Tallinn',
      education: 'Group',
      github: 'github.com/example',
      telegram: '@example',
      website: 'example.com',
      skillsText: 'C#, SQL',
      showInLeaderboard: false,
      showBio: true,
      showLocation: true,
      showEducation: true,
      showGithub: true,
      showTelegram: true,
      showWebsite: true,
      showSkills: true,
      showStats: false,
    });

    const parsed = parseProfileExtra(encoded);
    expect(parsed).toMatchObject({
      publicProfileEnabled: false,
      showInLeaderboard: false,
      showBio: true,
      showLocation: true,
      showEducation: true,
      showGithub: true,
      showTelegram: true,
      showWebsite: true,
      showSkills: true,
      showStats: false,
      skillsText: 'C#, SQL',
    });
  });
});
