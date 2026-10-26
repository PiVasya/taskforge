import math
import unittest

import numpy as np

from render import Mob, transformed


class MobAppearanceTests(unittest.TestCase):
    def test_four_unarmed_mobs_have_no_handheld_item_meshes(self):
        for name in ("wither-skeleton", "stray", "parched", "illusioner"):
            mob = Mob(name)
            with self.subTest(mob=name):
                for phase in (0, math.pi / 2, math.pi):
                    textures = [texture for texture, _ in mob.batches(phase)]
                    self.assertFalse(any(texture.startswith("items/") for texture in textures))
                    self.assertNotIn("entity/trident.png", textures)

    def test_tridents_are_preserved_for_drowned_and_zombie(self):
        for name in ("drowned", "trident-zombie"):
            with self.subTest(mob=name):
                batches = Mob(name).batches(0)
                self.assertTrue(any(texture == "entity/trident.png" and faces for texture, faces in batches))

    def test_pumpkin_is_raised_one_model_texel_in_all_poses(self):
        mob = Mob("snow-golem")
        for phase in (0, math.pi / 2, math.pi):
            head_inverse = np.linalg.inv(mob.matrices(phase)["head"])
            points = np.concatenate([
                vertices
                for texture, faces in mob.batches(phase)
                if texture.startswith("blocks/pumpkin_")
                for vertices, _, _ in faces
            ])
            local = transformed(points, head_inverse)
            np.testing.assert_allclose(local.min(axis=0), [-4.5, 20.5, -4.5], atol=1e-8)
            np.testing.assert_allclose(local.max(axis=0), [4.5, 29.5, 4.5], atol=1e-8)

    def test_unarmed_mobs_still_animate(self):
        for name in ("wither-skeleton", "stray", "parched", "illusioner"):
            mob = Mob(name)
            def points(phase):
                return np.concatenate([vertices for _, faces in mob.batches(phase) for vertices, _, _ in faces])
            with self.subTest(mob=name):
                self.assertFalse(np.allclose(points(0), points(math.pi / 2)))


if __name__ == "__main__":
    unittest.main()
