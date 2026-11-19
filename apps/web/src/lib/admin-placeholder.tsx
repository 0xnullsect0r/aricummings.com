"use client";

// Build-time stand-in for the admin SPA, used only while src/features/admin
// does not exist in this tree (see next.config.ts). It renders an explicit
// not-present notice - it is not the admin app.
export default function AdminPlaceholder() {
  return (
    <div
      style={{
        minHeight: "100vh",
        display: "flex",
        flexDirection: "column",
        gap: "0.75rem",
        justifyContent: "center",
        padding: "0 1.5rem",
        backgroundColor: "#0A0A0B",
        color: "#EDEDE6",
        fontFamily: "monospace",
      }}
    >
      <p style={{ margin: 0, color: "#85857C", fontSize: 11, letterSpacing: "0.08em" }}>
        ADMIN
      </p>
      <h1 style={{ margin: 0, fontSize: "1.25rem" }}>ADMIN APP NOT PRESENT IN THIS BUILD</h1>
      <p style={{ margin: 0, color: "#B9B9B0", maxWidth: "40rem" }}>
        src/features/admin is built by the admin-spa area and has not been merged into this tree
        yet.
      </p>
    </div>
  );
}
