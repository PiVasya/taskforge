# Rating read-model deployment

This release is based on `taskforge-develop-320-rating-recalculation-fixed.zip`.
The Solutions API owns `RatingAcceptedActivities`; the worker and Tasks API do
not own this database schema.

1. On the developer workstation, from the repository root:
   `./scripts/generate-migrations.sh AddRatingAcceptedActivities solutions`
   (never use `generate-migrations-force.sh`). Inspect the generated EF
   migration for a composite `(SourceKind, SourceId)` key, indexes on
   `(UserId, AssignmentId)`, `(CourseId, SubmittedAt)`, and `SubmittedAt`.
2. Commit the generated migration and ModelSnapshot produced by EF; run CI.
   This archive deliberately preserves the user-owned EF migration history.
3. Deploy the normal application release **after** migration generation. The
   deployment must include `tasks-api`, `solutions-api`, and `rating-worker`.
   Tasks API must expose `attemptId` and `courseId` from its internal passed
   activity endpoint before rating-worker starts.
4. On first start the worker automatically builds `UserRatings` and
   `RatingAcceptedActivities`. Until completion the public leaderboard returns
   `503 RATING_PROJECTION_BUILDING` without consuming user energy.
5. In the existing admin user management screen, you can manually trigger
   a full rebuild and inspect completion status. The marker
   `RatingAcceptedActivitiesV1` is written only after the full rebuild ends.

Read path: global / group use stored `UserRatings`; course / date filters
aggregate previously stored accepted activity in SQL. User visibility and
search are applied from Identity API using batches of at most 1000 user IDs;
missing Identity API responses fail rather than exposing hidden users.

Source changes (verdicts, task prices, deleted tasks, deleted courses) are
applied asynchronously through the existing dirty-user queue and full-rebuild
mechanism; the read model is therefore eventually consistent, not instant.
