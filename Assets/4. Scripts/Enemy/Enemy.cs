using UnityEngine;

public class Enemy : MonoBehaviour
{
    [HideInInspector] public int motherListIndex = -1;
    public float maxHealth = 100f;
    private float _currentHealth;

    private Transform _targetPlayer;
    private EnemyMother _myMother;

    public void InitializeEnemy(EnemyMother myMother, Transform targetPlayer)
    {
        _myMother = myMother;
        _targetPlayer = targetPlayer;
        _currentHealth = maxHealth;
    }

    public int GetTargetIndex()
    {
        if (_targetPlayer == null || !_targetPlayer.gameObject.activeInHierarchy)
        {
            _targetPlayer = EnemyMother.GetClosestTarget(transform.position);
        }
        return EnemyMother.ValidTargets.IndexOf(_targetPlayer);
    }

    public void TakeDamage(float damageAmount)
    {
        _currentHealth -= damageAmount;
        if (_currentHealth <= 0)
        {
            if (motherListIndex != -1 && _myMother != null) _myMother.RemoveEnemy(this);
            Destroy(gameObject);
        }
    }
}