// Neural physics and its WebGL shaders work in CSS pixels, not backing-store pixels.
export function mapClientToNeuralSpace(clientX, clientY, rect, width, height) {
  if (!rect || rect.width <= 0 || rect.height <= 0) return null;
  return {
    x: (clientX - rect.left) * (width / rect.width),
    y: (clientY - rect.top) * (height / rect.height),
  };
}

export function createNeuralPointerTracker(canvas, getSize) {
  const pointer = {
    x: 0, y: 0, vx: 0, vy: 0,
    down: false, rdown: false, has: false,
    clientX: 0, clientY: 0,
  };

  function project(clientX, clientY) {
    const { width, height } = getSize();
    return mapClientToNeuralSpace(
      clientX, clientY, canvas.getBoundingClientRect(), width, height,
    );
  }

  function setPointer(clientX, clientY) {
    const local = project(clientX, clientY);
    if (!local) return;
    pointer.vx = pointer.has ? local.x - pointer.x : 0;
    pointer.vy = pointer.has ? local.y - pointer.y : 0;
    pointer.has = true;
    pointer.clientX = clientX;
    pointer.clientY = clientY;
    pointer.x = local.x;
    pointer.y = local.y;
  }

  function reprojectPointer() {
    if (!pointer.has) return;
    const local = project(pointer.clientX, pointer.clientY);
    if (!local) return;
    pointer.x = local.x;
    pointer.y = local.y;
    pointer.vx = 0;
    pointer.vy = 0;
  }

  return { pointer, setPointer, reprojectPointer };
}
