import React, { useEffect, useRef, useState } from 'react';
import { createAetherRenderer } from './aetherRenderer';

export default function BgFxAetherWebgl({ enabled, paletteKey }) {
  const canvasRef = useRef(null);
  const [generation, setGeneration] = useState(0);

  useEffect(() => {
    if (!enabled || !canvasRef.current) return undefined;
    return createAetherRenderer(canvasRef.current, {
      onFailure: (message) => console.warn('[AETHER]', message),
      onRestore: () => setGeneration((previous) => previous + 1),
    });
  }, [enabled, generation, paletteKey]);

  if (!enabled) return null;
  return (
    <canvas
      key={generation}
      ref={canvasRef}
      aria-hidden="true"
      className="bgfx-aether-canvas"
      data-aether-preset="theme-28-100-100-100"
    />
  );
}
