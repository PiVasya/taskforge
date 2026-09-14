import { getSolutionDetails, getAdminImageSolutionDetails } from './admin';
import { getAdminTaskTestAttemptReview } from './taskTestAttempts';
import { getAdminMathAttemptReview } from './mathTaskAttempts';
import { getAdminUser } from './adminUsers';

export { subscribeAdminSolutionEvents } from './adminSolutionEvents';
export async function loadAdminSolutionLiveDetail(event) {
  const kind = String(event?.kind || '').toLowerCase();
  if (kind === 'test') return getAdminTaskTestAttemptReview(event.itemId);
  if (kind === 'math') return getAdminMathAttemptReview(event.itemId);
  if (kind === 'image') return getAdminImageSolutionDetails(event.itemId);
  return getSolutionDetails(event.itemId);
}

export { getAdminUser };
