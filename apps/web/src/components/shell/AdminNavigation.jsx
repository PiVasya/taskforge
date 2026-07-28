import React from 'react';
import { Link } from 'react-router-dom';
import { useShellNavigation } from './navigation';

function AdminNavigation() {
  const { isAdminArea, adminNav } = useShellNavigation();
  if (!isAdminArea || adminNav.length === 0) return null;

  return (
    <nav className="admin-mobile-tabs xl:hidden" aria-label="Разделы админ-панели">
      <div className="container-app admin-mobile-tabs__scroller">
        {adminNav.map((item) => (
          <Link
            key={item.to}
            to={item.to}
            className={`admin-mobile-tab ${item.active ? 'is-active' : ''}`}
          >
            <item.icon size={16} />
            <span>{item.label}</span>
          </Link>
        ))}
      </div>
    </nav>
  );
}

export default React.memo(AdminNavigation);
