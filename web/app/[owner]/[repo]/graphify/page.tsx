import { decodeRouteSegment } from "@/lib/repo-route";

interface GraphifyPageProps {
  params: Promise<{
    owner: string;
    repo: string;
  }>;
  searchParams: Promise<{
    branch?: string;
  }>;
}

export default async function GraphifyPage({ params, searchParams }: GraphifyPageProps) {
  const { owner, repo } = await params;
  const decodedOwner = decodeRouteSegment(owner);
  const decodedRepo = decodeRouteSegment(repo);
  const resolvedSearchParams = await searchParams;

  const paramsForApi = new URLSearchParams();
  if (resolvedSearchParams?.branch) {
    paramsForApi.set("branch", resolvedSearchParams.branch);
  }

  const graphifySrc = `/api/v1/repos/${encodeURIComponent(decodedOwner)}/${encodeURIComponent(decodedRepo)}/graphify${
    paramsForApi.toString() ? `?${paramsForApi.toString()}` : ""
  }`;

  return (
    <div className="min-h-[calc(100vh-8rem)] overflow-hidden rounded-xl border border-border/70 bg-card shadow-sm">
      <iframe
        title={`${decodedOwner}/${decodedRepo} Graphify`}
        src={graphifySrc}
        className="h-[calc(100vh-8rem)] w-full bg-background"
      />
    </div>
  );
}
