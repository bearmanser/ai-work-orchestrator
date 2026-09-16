import { Navigate, Outlet } from "react-router-dom";
import { useSession } from "./useSession";

/**
 * Route wrapper: every admin page is mounted inside it so unauthenticated
 * visitors are bounced to /login before any protected content renders.
 */
export function RequireSession() {
  const { session, isLoading } = useSession();

  if (isLoading) {
    return <p>Loading…</p>;
  }
  if (session === null) {
    return <Navigate to="/login" replace />;
  }
  return <Outlet />;
}
