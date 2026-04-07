using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class HandAnimation : MonoBehaviour
{
    [SerializeField] private InputActionReference gripReference;
    [SerializeField] private InputActionReference triggerReference;
    [SerializeField] private Animator handAnimator;
    void Update()
    {
        float gripValue = gripReference.action.ReadValue<float>(); 
       // Debug.Log(gripValue);
       handAnimator.SetFloat("Grip", gripValue);
       
       float triggerValue = triggerReference.action.ReadValue<float>(); 
       // Debug.Log(gripValue);
       handAnimator.SetFloat("Trigger", triggerValue);
    }
}
