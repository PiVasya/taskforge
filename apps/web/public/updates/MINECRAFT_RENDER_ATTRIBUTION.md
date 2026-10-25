# Minecraft mob render attribution

Mob cards use static renders by default. While the card is visible, it also displays an animated spin render and falls back to the static headshot when needed.

- Static renders: `IsaiahPapa/minecraft-render-pipeline`, generated from Mojang sample assets, plus fallback renders previously used on the site when needed.
- Animated renders: `IsaiahPapa/minecraft-render-pipeline` (`spin.gif`) for all mob cards in the showcase.

No canvas, WebGL, frame compositor, continuous JavaScript animation loop, or server-side rendering is used.
