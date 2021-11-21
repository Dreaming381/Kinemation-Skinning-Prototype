using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Latios.Kinemation
{
    public class HybridSkinningToggle : MonoBehaviour
    {
        public bool m_enableHybrid   = false;
        public bool m_enableBlending = false;

        private static HybridSkinningToggle instance = null;

        public static bool EnableHybrid
        {
            get
            {
                if (instance == null)
                {
                    instance = FindObjectOfType<HybridSkinningToggle>();
                    if (instance == null)
                        return false;
                }
                return instance.m_enableHybrid;
            }
        }

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

