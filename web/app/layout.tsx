import type { Metadata } from "next";
import RouteProviders from "@/app/route-providers";
import "./globals.css";

export const metadata: Metadata = {
  title: "OpenDeepWiki",
  description: "AI-powered code knowledge base for repository analysis and documentation generation",
  icons: {
    icon: "/favicon.png",
  },
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en" suppressHydrationWarning>
      <body className="antialiased">
        <RouteProviders>{children}</RouteProviders>
      </body>
    </html>
  );
}
