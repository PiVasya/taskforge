import React from 'react';
import { Link } from 'react-router-dom';
import { ChevronRight } from 'lucide-react';
import { useUiNavigationSettings } from '../../contexts/UiSettingsContext';
import { useShellNavigation } from './navigation';

const SideNavLink = React.memo(function SideNavLink({
  to,
  icon: Icon,
  label,
  subtitle,
  active,
  rail,
}) {
  const title = subtitle ? `${label} — ${subtitle}` : label;
  return (
    <Link
      to={to}
      className={`side-nav-link ${active ? 'is-active' : ''} is-compact ${rail ? 'is-rail' : ''}`}
      title={title}
    >
      <span className="side-nav-icon">
        <Icon size={18} />
      </span>
      {!rail && (
        <span className="min-w-0 flex-1">
          <span className="side-nav-label is-compact">{label}</span>
        </span>
      )}
      {!rail && <ChevronRight size={16} className="side-nav-chevron" />}
    </Link>
  );
});

function DesktopSidebar() {
  const { sidebarCollapsed } = useUiNavigationSettings();
  const { primaryNav, adminPrimaryNav } = useShellNavigation();

  return (
    <aside
      className={`dashboard-sticky-rail hidden xl:flex xl:flex-col gap-4 sticky top-24 self-start ${sidebarCollapsed ? 'w-[4.75rem]' : ''}`}
    >
      <div className={`card ${sidebarCollapsed ? 'p-2' : 'p-3'}`}>
        {!sidebarCollapsed && <div className="side-nav-section-title">Основное</div>}
        <div className={sidebarCollapsed ? 'space-y-1.5' : 'mt-2 space-y-1.5'}>
          {primaryNav.map((item) => (
            <SideNavLink key={item.to} {...item} rail={sidebarCollapsed} />
          ))}
        </div>
      </div>

      {adminPrimaryNav.length > 0 && (
        <div className={`card ${sidebarCollapsed ? 'p-2' : 'p-3'}`}>
          {!sidebarCollapsed && <div className="side-nav-section-title">Админка</div>}
          <div className={sidebarCollapsed ? 'space-y-1.5' : 'mt-2 space-y-1.5'}>
            {adminPrimaryNav.map((item) => (
              <SideNavLink key={item.to} {...item} rail={sidebarCollapsed} />
            ))}
          </div>
        </div>
      )}
    </aside>
  );
}

export default React.memo(DesktopSidebar);
