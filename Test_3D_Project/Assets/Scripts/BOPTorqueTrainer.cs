using UnityEngine;
using UnityEngine.InputSystem; // Nuevo namespace requerido
using System.Collections;
using System.Collections.Generic;

public class BOPTorqueTrainer : MonoBehaviour
{
    [Header("Configuración General")]
    [Tooltip("Arrastra aquí tu cámara principal.")]
    public Camera mainCamera;

    [Header("Secuencia de Entrenamiento")]
    [Tooltip("Arrastra aquí los objetos de las tuercas en el ORDEN CORRECTO (1 al 8) en el que deben ser apretadas.")]
    public Transform[] targetSequence;

    [Header("Animación de Ajuste")]
    [Tooltip("Distancia en unidades que se moverá la tuerca en su eje Z local.")]
    public float moveDistance = 0.15f;
    [Tooltip("Tiempo en segundos que tomará la animación.")]
    public float moveDuration = 1.0f;
    [Tooltip("Velocidad y dirección del giro (grados por segundo). Positivo = horario, Negativo = antihorario.")]
    public float rotationSpeed = 360f;

    [Header("Feedback Visual (Opcional)")]
    [Tooltip("Color cuando la tuerca está en estado neutral.")]
    public Color normalColor = Color.gray;
    [Tooltip("Color al pasar el mouse por encima.")]
    public Color hoverColor = Color.yellow;
    [Tooltip("Color cuando se acierta el orden.")]
    public Color successColor = Color.green;
    [Tooltip("Color cuando se equivoca.")]
    public Color errorColor = Color.red;

    [Header("Feedback de Audio")]
    [Tooltip("Sonido al acertar el orden correcto.")]
    public AudioClip successClip;
    [Tooltip("Sonido al equivocarse.")]
    public AudioClip errorClip;
    [Tooltip("Volumen de los efectos (0 a 1).")]
    [Range(0f, 1f)] public float sfxVolume = 1.0f;

    // Estado interno del juego
    private int _currentIndex = 0;
    private bool _isGameActive = true;
    private Transform _currentHoveredObject;
    private AudioSource _audioSource; // Referencia al componente de audio

    // Diccionario para guardar los colores originales y renderers para optimización
    private Dictionary<Transform, Renderer> _nutRenderers = new Dictionary<Transform, Renderer>();
    private HashSet<Transform> _completedNuts = new HashSet<Transform>();

    void Start()
    {
        InitializeSystem();
    }

    void Update()
    {
        if (!_isGameActive) return;

        HandleInput();
    }

    /// <summary>
    /// Configuración inicial por código para evitar asignaciones manuales de componentes.
    /// </summary>
    void InitializeSystem()
    {
        // 1. Autodetectar cámara si no se asignó
        if (mainCamera == null) mainCamera = Camera.main;

        // Configuración automática del AudioSource
        _audioSource = GetComponent<AudioSource>();
        if (_audioSource == null)
        {
            // Si no existe, lo creamos por código
            _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
        }

        // 2. Validar que tenemos tuercas asignadas
        if (targetSequence == null || targetSequence.Length == 0)
        {
            Debug.LogError("FATAL: No has asignado la secuencia de tuercas en el Inspector.");
            _isGameActive = false;
            return;
        }

        // 3. Configurar los materiales y colliders automáticamente
        foreach (Transform nut in targetSequence)
        {
            if (nut == null) continue;

            // Asegurar que tenga Collider para el Raycast
            if (nut.GetComponent<Collider>() == null)
            {
                // Agregamos un MeshCollider automáticamente si no tiene
                nut.gameObject.AddComponent<MeshCollider>();
            }

            // Guardar referencia al Renderer para cambiar colores
            Renderer rend = nut.GetComponent<Renderer>();
            if (rend != null)
            {
                // Clonamos el material para no afectar los assets originales
                rend.material = new Material(rend.material);
                rend.material.color = normalColor;
                _nutRenderers[nut] = rend;
            }
        }

        //Debug.Log($"Sistema iniciado. Secuencia de {targetSequence.Length} pasos lista.");
    }

    /// <summary>
    /// Maneja el Raycast y la lógica de interacción del mouse.
    /// </summary>
    void HandleInput()
    {
        if (Mouse.current == null) return;

        Vector2 mousePosition = Mouse.current.position.ReadValue();
        Ray ray = mainCamera.ScreenPointToRay(mousePosition);
        RaycastHit hit;

        // Lógica de Hover (Pasar el mouse por encima)
        if (Physics.Raycast(ray, out hit))
        {
            Transform hitTransform = hit.transform;

            // Si estamos mirando una tuerca que es parte de nuestra lista y no ha sido completada
            if (_nutRenderers.ContainsKey(hitTransform) && !_completedNuts.Contains(hitTransform))
            {
                SetHoverState(hitTransform);

                // Lógica de Clic (Apretar tuerca)
                if (Mouse.current.leftButton.wasPressedThisFrame)
                {
                    ValidateAction(hitTransform);
                }
            }
            else
            {
                ClearHover();
            }
        }
        else
        {
            ClearHover();
        }
    }

    /// <summary>
    /// Valida si la tuerca clickeada es la correcta en la secuencia.
    /// </summary>
    void ValidateAction(Transform selectedNut)
    {
        // El objetivo actual esperado es el índice actual del array
        Transform expectedNut = targetSequence[_currentIndex];

        if (selectedNut == expectedNut)
        {
            // --- ACIERTO ---
            Debug.Log($"<color=green>¡Correcto! Paso {_currentIndex + 1} completado.</color>");

            PlaySound(successClip); // Reproducir sonido de éxito

            // Efecto visual permanente
            _completedNuts.Add(selectedNut);
            SetNutColor(selectedNut, successColor); // Color de éxito inmediato

            // INICIAR ANIMACIÓN
            StartCoroutine(AnimateNut(selectedNut));

            _currentIndex++;

            if (_currentIndex >= targetSequence.Length)
            {
                OnTrainingComplete();
            }
        }
        else
        {
            // --- ERROR ---
            Debug.Log($"<color=red>¡Error! Secuencia incorrecta. Debías apretar el perno {_currentIndex + 1}.</color>");
            PlaySound(errorClip); // Reproducir sonido de error
            StartCoroutine(FlashError(selectedNut));
        }
    }

    void PlaySound(AudioClip clip)
    {
        if (clip != null && _audioSource != null)
        {
            _audioSource.PlayOneShot(clip, sfxVolume);
        }
    }

    /// <summary>
    /// Corrutina que maneja el movimiento y rotación de la tuerca.
    /// </summary>
    IEnumerator AnimateNut(Transform nut)
    {
        float elapsedTime = 0f;
        Vector3 startLocalPos = nut.localPosition;

        // Calculamos la posición final sumando distancia en el eje Z local (Vector3.forward)
        // Nota: Vector3.forward es (0,0,1). Al multiplicar por moveDistance y sumar, afectamos Z.
        Vector3 endLocalPos = startLocalPos + (Vector3.forward * moveDistance);

        while (elapsedTime < moveDuration)
        {
            // Avanzar tiempo
            elapsedTime += Time.deltaTime;
            float t = elapsedTime / moveDuration;

            // 1. MOVIMIENTO (Lerp para suavizado)
            nut.localPosition = Vector3.Lerp(startLocalPos, endLocalPos, t);

            // 2. ROTACIÓN (Girar sobre su eje Z local)
            // Multiplicamos la velocidad por el tiempo delta para un giro suave frame a frame
            nut.Rotate(Vector3.forward, rotationSpeed * Time.deltaTime, Space.Self);

            yield return null; // Esperar al siguiente frame
        }

        // Asegurar posición final exacta para evitar errores de punto flotante
        nut.localPosition = endLocalPos;
        // NOTA: No forzamos rotación final para que quede donde "cayó" el giro, 
        // dando un aspecto más natural de apriete.
    }

    /// <summary>
    /// Se ejecuta al terminar la secuencia.
    /// </summary>
    void OnTrainingComplete()
    {
        Debug.Log("<b>¡ENTRENAMIENTO COMPLETADO CON ÉXITO!</b>");
        _isGameActive = false;
        // Aquí podrías llamar a una UI de victoria o reiniciar la escena
    }

    // --- SISTEMA VISUAL ---

    void SetHoverState(Transform target)
    {
        if (_currentHoveredObject != target)
        {
            ClearHover(); // Limpiar el anterior
            _currentHoveredObject = target;
            SetNutColor(target, hoverColor);
        }
    }

    void ClearHover()
    {
        if (_currentHoveredObject != null)
        {
            // Solo restaurar color si no está completada
            if (!_completedNuts.Contains(_currentHoveredObject))
            {
                SetNutColor(_currentHoveredObject, normalColor);
            }
            _currentHoveredObject = null;
        }
    }

    IEnumerator FlashError(Transform target)
    {
        // Feedback visual de error parpadeante
        SetNutColor(target, errorColor);
        yield return new WaitForSeconds(0.5f);

        // Regresar a estado normal o hover si el mouse sigue ahí
        if (!_completedNuts.Contains(target))
        {
            SetNutColor(target, _currentHoveredObject == target ? hoverColor : normalColor);
        }
    }

    void SetNutColor(Transform t, Color c)
    {
        if (_nutRenderers.ContainsKey(t))
        {
            _nutRenderers[t].material.color = c;
        }
    }
}