import React from 'react';
import { Navigate } from 'react-router-dom';

export default function ProfilePage() {
  return <Navigate to="/settings?section=profile" replace />;
}
