export function rolesFromProfile(user) {
  const normalize = value => Array.isArray(value)
    ? value.flatMap(normalize)
    : typeof value === 'string' ? value.split(/[,;]+/).map(role => role.trim()).filter(Boolean) : [];
  const roles = [...normalize(user?.role), ...normalize(user?.roles)];
  if (roles.some(role => role.toLowerCase() === 'superadmin')) roles.push('Admin');
  return roles.filter((role, index) => roles.findIndex(other => other.toLowerCase() === role.toLowerCase()) === index);
}
