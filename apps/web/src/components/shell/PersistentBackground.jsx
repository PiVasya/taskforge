import React, { useLayoutEffect, useMemo } from 'react';
import BgFxCanvas from '../bgfx/BgFxCanvas';
import { useUiBackgroundSettings } from '../../contexts/UiSettingsContext';

function PersistentBackground() {
  const {
    bgFx,
    fxMode,
    fxVariant,
    paletteKey,
  } = useUiBackgroundSettings();

  const variant = useMemo(
    () => (fxMode === 'random' ? Math.floor(Math.random() * 10) : Number(fxVariant) || 0),
    [bgFx, fxMode, fxVariant],
  );

  const aetherActive = bgFx && variant === 9;

  useLayoutEffect(() => {
    document.documentElement.dataset.aetherBg = aetherActive ? '1' : '0';
    return () => { delete document.documentElement.dataset.aetherBg; };
  }, [aetherActive]);

  if (!bgFx) return null;

  return (
    <div className="pointer-events-none fixed inset-0 z-0 overflow-hidden" aria-hidden>
      {aetherActive ? null : <div className="absolute inset-0 bg-gradient-to-b from-brand-600/12 via-transparent to-transparent blur-2xl" />}
      <div className="absolute inset-0">
        <BgFxCanvas
          enabled
          variant={variant}
          paletteKey={paletteKey}
        />
      </div>
    </div>
  );
}

export default React.memo(PersistentBackground);
