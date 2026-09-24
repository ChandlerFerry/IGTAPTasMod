import type { Metadata } from "next";
import "./globals.css";
import "./optimizer.css";
import "./pause.css";

export const metadata: Metadata = {
  title: "IGTAP TAS Studio",
  description: "Record, replay, and optimize IGTAP routes from your browser.",
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return <html lang="en"><body>{children}</body></html>;
}
