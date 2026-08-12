import React from 'react';
import { createPortal } from 'react-dom';
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
  const [tooltip, setTooltip] = React.useState(null);

  const showTooltip = React.useCallback((event) => {
    if (!rail || typeof document === 'undefined') return;
    const rect = event.currentTarget.getBoundingClientRect();
    setTooltip({ left: rect.right + 12, top: rect.top + rect.height / 2 });
  }, [rail]);

  const hideTooltip = React.useCallback(() => setTooltip(null), []);

  return (
    <>
      <Link
        to={to}
        className={`side-nav-link ${active ? 'is-active' : ''} is-compact ${rail ? 'is-rail' : ''}`}
        title={rail ? undefined : title}
        aria-label={rail ? title : undefined}
        onMouseEnter={showTooltip}
        onMouseLeave={hideTooltip}
        onFocus={showTooltip}
        onBlur={hideTooltip}
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
      {rail && tooltip && typeof document !== 'undefined'
        ? createPortal(
            <div
              className="side-nav-rail-tooltip"
              role="tooltip"
              style={{ left: tooltip.left, top: tooltip.top }}
            >
              {title}
            </div>,
            document.body,
          )
        : null}
    </>
  );
});

function DesktopSidebar() {
  const { sidebarCollapsed } = useUiNavigationSettings();
  const { primaryNav, adminPrimaryNav } = useShellNavigation();

  return (
    <aside
      className={`dashboard-sticky-rail hidden xl:flex xl:flex-col gap-4 sticky top-24 self-start ${sidebarCollapsed ? 'w-[4.75rem]' : ''}`}
    >
      <div className={`card side-nav-panel ${sidebarCollapsed ? 'p-2' : 'p-3'}`}>
        {!sidebarCollapsed && <div className="side-nav-section-title">Основное</div>}
        <div className={sidebarCollapsed ? 'space-y-1' : 'mt-2 space-y-1.5'}>
          {primaryNav.map((item) => (
            <SideNavLink key={item.to} {...item} rail={sidebarCollapsed} />
          ))}
        </div>
      </div>

      {adminPrimaryNav.length > 0 && (
        <div className={`card side-nav-panel ${sidebarCollapsed ? 'p-2' : 'p-3'}`}>
          {!sidebarCollapsed && <div className="side-nav-section-title">Админка</div>}
          <div className={sidebarCollapsed ? 'space-y-1' : 'mt-2 space-y-1.5'}>
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
