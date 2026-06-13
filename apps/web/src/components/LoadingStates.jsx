import React from 'react';
import { Card } from './ui';

export function CourseSkeletonGrid({ count = 6 }) {
  return (
    <div className="auto-fill-grid">
      {Array.from({ length: count }).map((_, index) => (
        <Card key={index} className="min-h-[150px] p-5 tf-skeleton-card">
          <div className="tf-skeleton h-5 w-3/4 rounded-full" />
          <div className="mt-4 space-y-2">
            <div className="tf-skeleton h-3 w-full rounded-full" />
            <div className="tf-skeleton h-3 w-5/6 rounded-full" />
            <div className="tf-skeleton h-3 w-2/3 rounded-full" />
          </div>
        </Card>
      ))}
    </div>
  );
}

export function LeaderboardSkeletonGrid({ count = 6 }) {
  return (
    <div className="grid gap-4 md:grid-cols-2">
      {Array.from({ length: count }).map((_, index) => (
        <Card key={index} className="p-4 tf-skeleton-card">
          <div className="flex items-center gap-4">
            <div className="tf-skeleton h-14 w-14 rounded-full" />
            <div className="min-w-0 flex-1">
              <div className="tf-skeleton h-4 w-2/3 rounded-full" />
              <div className="mt-3 flex gap-2">
                <div className="tf-skeleton h-5 w-24 rounded-full" />
                <div className="tf-skeleton h-5 w-20 rounded-full" />
                <div className="tf-skeleton h-5 w-28 rounded-full" />
              </div>
            </div>
          </div>
        </Card>
      ))}
    </div>
  );
}

export function SolutionSkeletonList({ count = 4 }) {
  return (
    <Card className="p-4 space-y-4">
      {Array.from({ length: count }).map((_, index) => (
        <div key={index} className="rounded-xl border border-neutral-200 dark:border-neutral-700 p-4 tf-skeleton-card">
          <div className="flex items-center justify-between gap-4">
            <div className="flex-1 min-w-0">
              <div className="tf-skeleton h-4 w-2/3 rounded-full" />
              <div className="tf-skeleton mt-3 h-3 w-1/3 rounded-full" />
            </div>
            <div className="hidden sm:flex gap-2">
              <div className="tf-skeleton h-7 w-24 rounded-full" />
              <div className="tf-skeleton h-7 w-28 rounded-full" />
            </div>
          </div>
        </div>
      ))}
    </Card>
  );
}
