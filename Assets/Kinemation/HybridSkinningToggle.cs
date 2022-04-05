using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Latios.Kinemation
{
    public class HybridSkinningToggle : MonoBehaviour
    {
        public bool m_enableBlending = false;

        private static HybridSkinningToggle instance = null;

        public static bool EnableBlending
        {
            get
            {
                if (instance == null)
                {
                    instance = FindObjectOfType<HybridSkinningToggle>();
                    if (instance == null)
                        return false;
                }
                return instance.m_enableBlending;
            }
        }
    }
}

