// Placeholder declaration for the admin SPA entry, owned by the admin-spa
// area (apps/web/src/features/admin). It only exists so the fixed loader route
// (src/app/admin/[[...slug]]/page.tsx) type-checks before that folder lands.
// Once src/features/admin exists, TypeScript resolves the real module and this
// file can be deleted.
declare module "@/features/admin" {
  import type { ComponentType } from "react";
  const Admin: ComponentType;
  export default Admin;
}
