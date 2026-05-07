import { decodeRouteSegment } from "@/lib/repo-route";
import { fetchGraphifyReport } from "@/lib/repository-api";
import { MarkdownRenderer } from "@/components/repo/markdown-renderer";
import Link from "next/link";

interface GraphifyPageProps {
  params: Promise<{
    owner: string;
    repo: string;
  }>;
  searchParams: Promise<{
    branch?: string;
    lang?: string;
    view?: string;
  }>;
}

export default async function GraphifyPage({ params, searchParams }: GraphifyPageProps) {
  const { owner, repo } = await params;
  const decodedOwner = decodeRouteSegment(owner);
  const decodedRepo = decodeRouteSegment(repo);
  const resolvedSearchParams = await searchParams;
  const currentView = resolvedSearchParams?.view === "report" ? "report" : "graph";

  const paramsForApi = new URLSearchParams();
  if (resolvedSearchParams?.branch) {
    paramsForApi.set("branch", resolvedSearchParams.branch);
  }

  const graphifySrc = `/api/v1/repos/${encodeURIComponent(decodedOwner)}/${encodeURIComponent(decodedRepo)}/graphify${
    paramsForApi.toString() ? `?${paramsForApi.toString()}` : ""
  }`;

  const graphParams = new URLSearchParams();
  const reportParams = new URLSearchParams();
  if (resolvedSearchParams?.branch) {
    graphParams.set("branch", resolvedSearchParams.branch);
    reportParams.set("branch", resolvedSearchParams.branch);
  }
  if (resolvedSearchParams?.lang) {
    graphParams.set("lang", resolvedSearchParams.lang);
    reportParams.set("lang", resolvedSearchParams.lang);
  }
  reportParams.set("view", "report");

  const graphHref = `?${graphParams.toString()}`;
  const reportHref = `?${reportParams.toString()}`;

  let reportContent: string | null = null;
  if (currentView === "report") {
    try {
      reportContent = await fetchGraphifyReport(decodedOwner, decodedRepo, resolvedSearchParams?.branch);
    } catch {
      reportContent = null;
    }
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap gap-2">
        <Link
          href={graphHref}
          className={`rounded-md border px-3 py-2 text-sm font-medium transition-colors ${
            currentView === "graph"
              ? "border-primary bg-primary text-primary-foreground"
              : "border-border bg-background hover:bg-muted"
          }`}
        >
          Graph
        </Link>
        <Link
          href={reportHref}
          className={`rounded-md border px-3 py-2 text-sm font-medium transition-colors ${
            currentView === "report"
              ? "border-primary bg-primary text-primary-foreground"
              : "border-border bg-background hover:bg-muted"
          }`}
        >
          Report
        </Link>
      </div>

      {currentView === "report" ? (
        <article className="rounded-xl border border-border/70 bg-card p-6 shadow-sm">
          {reportContent ? (
            <MarkdownRenderer content={reportContent} />
          ) : (
            <p className="text-sm text-muted-foreground">Graphify report is not available yet.</p>
          )}
        </article>
      ) : (
        <div className="min-h-[calc(100vh-10rem)] overflow-hidden rounded-xl border border-border/70 bg-card shadow-sm">
          <iframe
            title={`${decodedOwner}/${decodedRepo} Graphify`}
            src={graphifySrc}
            className="h-[calc(100vh-10rem)] w-full bg-background"
          />
        </div>
      )}
    </div>
  );
}
