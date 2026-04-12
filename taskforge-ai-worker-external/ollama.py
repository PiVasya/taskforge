"""Compatibility shim for legacy imports.

The external worker now uses ``llm_client`` as the canonical import path. This
module stays in place so existing code and older patches importing ``ollama`` do
not break immediately.
"""
from llm_client import *  # noqa: F401,F403
