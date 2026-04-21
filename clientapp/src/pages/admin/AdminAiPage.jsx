import React from 'react';
import { Navigate } from 'react-router-dom';
import { usePageTitle } from '../../hooks/usePageTitle';

export default function AdminAiPage() {
  usePageTitle('TaskForge · AI центр');
  return <Navigate to="/admin/ai/chat" replace />;
}
