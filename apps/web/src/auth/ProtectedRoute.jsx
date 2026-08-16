import React from "react";
import { Navigate, Outlet, useLocation } from "react-router-dom";
import { useAuth } from "./AuthContext";
import { loginPathForLocation } from "./authRedirect";

export default function ProtectedRoute() {
    const { access, ready } = useAuth();
    const loc = useLocation();

    if (!ready) return <div className="container-app py-10 text-neutral-500">Загрузка…</div>;
    if (!access) return <Navigate to={loginPathForLocation(loc)} replace state={{ from: loc }} />;
    return <Outlet />;
}
