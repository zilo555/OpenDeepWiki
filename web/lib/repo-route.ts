export function encodePathSegment(value: string): string {
  return encodeURIComponent(value);
}

export function decodeRouteSegment(value: string): string {
  if (!value || !value.includes("%")) {
    return value;
  }

  try {
    return decodeURIComponent(value);
  } catch {
    return value;
  }
}

export function encodeSlugPath(slug: string): string {
  return slug
    .replace(/^[\s/]+|[\s/]+$/g, "")
    .split("/")
    .map((segment) => encodePathSegment(segment))
    .join("/");
}

export function buildRepoBasePath(owner: string, repo: string): string {
  return `/${encodePathSegment(owner)}/${encodePathSegment(repo)}`;
}

export function buildRepoDocPath(owner: string, repo: string, slug: string): string {
  return `${buildRepoBasePath(owner, repo)}/${encodeSlugPath(slug)}`;
}

export function buildRepoMindMapPath(owner: string, repo: string): string {
  return `${buildRepoBasePath(owner, repo)}/mindmap`;
}

export function buildRepoGraphifyPath(owner: string, repo: string): string {
  return `${buildRepoBasePath(owner, repo)}/graphify`;
}
