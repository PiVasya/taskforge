import { useEffect, useState } from 'react';

// Realtime search is local. Historical user lookup uses the existing backend
// endpoint, debounced while the operator types, never on the live tab.
export default function useAdminSolutionUserSearch(tab) {
  const [q, setQ] = useState('');
  const [submittedQuery, setSubmittedQuery] = useState('');
  const [userId, setUserId] = useState('');
  const [selectedUserInfo, setSelectedUserInfo] = useState(null);

  useEffect(() => {
    const text = q.trim();
    if (tab === 'live' || userId || text.length < 2) {
      setSubmittedQuery('');
      return undefined;
    }
    const timer = setTimeout(() => setSubmittedQuery(text), 300);
    return () => clearTimeout(timer);
  }, [q, tab, userId]);

  const clearSelectedUser = () => {
    setUserId('');
    setSelectedUserInfo(null);
  };
  const changeUserQuery = (text) => {
    setQ(text);
    if (userId) clearSelectedUser();
  };
  const clearUserSearch = () => {
    setQ('');
    setSubmittedQuery('');
    clearSelectedUser();
  };
  const chooseUser = (user, label) => {
    const id = String(user?.id || '');
    if (!id) return;
    setSelectedUserInfo({ ...user, id });
    setUserId(id);
    setQ(label);
  };

  return {
    q, submittedQuery, userId, selectedUserInfo,
    changeUserQuery, clearUserSearch, clearSelectedUser, chooseUser,
    submitSearch: () => setSubmittedQuery(q.trim()),
  };
}
