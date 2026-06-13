# Update 21 fix

Fixed the Redis/cache mega-update after CI failures.

- All .NET Dockerfiles now build from repository root and copy `Directory.Build.props`, `global.json`, and `services/shared` before restore/publish.
- The normal and manual full rebuild workflows use root build contexts for every .NET image that consumes shared code.
- Dev compose build contexts now match those Dockerfiles.
- Frontend list loading is paginated/progressive for courses, leaderboard, and my solutions.
- My solutions no longer eagerly loads code/tests/images/math lists at once; it loads the active tab first and loads other tabs when opened.
- Frontend debug logging remains disabled; there are no `console.log` or `TFDBG-FRONT` probes.
