import React from "react";
import ReactDOM from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import AuthProvider from "./auth/AuthContext";
import EditorModeProvider from "./contexts/EditorModeContext";
import App from "./App";
import "./index.css";

ReactDOM.createRoot(document.getElementById("root")).render(
    <BrowserRouter>
        <AuthProvider>
            
            <EditorModeProvider canEdit={true}>
                <App />
            </EditorModeProvider>
        </AuthProvider>
    </BrowserRouter>
);
