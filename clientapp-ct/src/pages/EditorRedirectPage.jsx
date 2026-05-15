import React, { useEffect } from 'react';
import { Navigate, useParams } from 'react-router-dom';
import { useEditorMode } from '../contexts/EditorModeContext';
import { getSectionPath, normalizeSectionCode } from '../data/ctSections';

export default function EditorRedirectPage() {
  const params = useParams();
  const { setEditorMode } = useEditorMode();
  const sectionCode = normalizeSectionCode(params.sectionCode || 'A1') || 'A1';

  useEffect(() => {
    setEditorMode(true);
  }, [setEditorMode]);

  return <Navigate to={getSectionPath(sectionCode)} replace />;
}
