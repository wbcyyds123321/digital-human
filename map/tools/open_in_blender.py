"""Startup helper: after the UI is ready, put every 3D viewport into CAM_HERO camera view
with rendered shading and a clean overlay."""
import bpy


def apply_view():
    scn = bpy.context.scene
    cam = bpy.data.objects.get("CAM_HERO") or next(
        (o for o in bpy.data.objects if o.type == "CAMERA"), None)
    if cam:
        scn.camera = cam
    switched = 0
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type != "VIEW_3D":
                continue
            for space in area.spaces:
                if space.type != "VIEW_3D":
                    continue
                try:
                    if space.region_3d:
                        space.region_3d.view_perspective = "CAMERA"
                    space.camera = cam
                    space.shading.type = "RENDERED"
                    space.overlay.show_overlays = False
                    space.overlay.show_relationship_lines = False
                    space.overlay.show_extras = False
                    space.overlay.show_floor = False
                    space.overlay.show_camera_passepartout = False
                    space.overlay.show_cursor = False
                    space.overlay.show_text = False
                    if space.region_3d:
                        space.region_3d.view_camera_zoom = 12
                    switched += 1
                except Exception as exc:  # noqa: BLE001
                    print("VIEW_SET_FAIL", repr(exc)[:120])
    print("BLENDER_GUI_READY viewports=%d camera=%s" % (switched, cam.name if cam else "-"))
    return None


bpy.app.timers.register(apply_view, first_interval=1.0)
