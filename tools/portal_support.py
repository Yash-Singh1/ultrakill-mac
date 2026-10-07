"""Select the portal cache only for the verified native renderer layout."""
from pathlib import Path


def supports_portal_cache(managed):
    raw = (Path(managed) / 'Assembly-CSharp.dll').read_bytes()
    return b'OnscreenPortalData\0' in raw and b'UpdateOcclusionBurst\0' in raw
