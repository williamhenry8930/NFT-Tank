﻿    /*  This file is part of the "Tanks Multiplayer" project by FLOBUK.
     *  You are only allowed to use these resources if you've bought them from the Unity Asset Store.
     * 	You shall not license, sublicense, sell, resell, transfer, assign, distribute or
     * 	otherwise make available to any third party the Service or the Content. */

    using UnityEngine;
    using System.Collections.Generic;
    using Photon.Pun;

    namespace TanksMP
    {
      /// <summary>
      /// Projectile script for player shots with collision/hit logic.
      /// </summary>
      public class Bullet : MonoBehaviourPun
      {
        /// <summary>
        /// Projectile travel speed in units.
        /// </summary>
        public float speed = 10;

        /// <summary>
        /// Damage to cause on a player that gets hit.
        /// </summary>
        public int damage = 3;

        /// <summary>
        /// Delay until despawned automatically when nothing gets hit.
        /// </summary>
        public float despawnDelay = 1f;

        /// <summary>
        /// Bounce count of walls and other environment obstactles.
        /// </summary>
        public int bounce = 0;

        /// <summary>
        /// Maximum amount of Players this bullet can hit on explosion.
        /// </summary>
        public int maxTargets = 1;

        /// <summary>
        /// Range within the explosion deals damage to other Players.
        /// The area is only checked if maxTargets is greater than 1.
        /// </summary>
        public float explosionRange = 1;

        /// <summary>
        /// Clip to play when a player gets hit.
        /// </summary>
        public AudioClip hitClip;

        /// <summary>
        /// Clip to play when this projectile gets despawned.
        /// </summary>
        public AudioClip explosionClip;

        /// <summary>
        /// Object to spawn when a player gets hit.
        /// </summary>
        public GameObject hitFX;

        /// <summary>
        /// Object to spawn when this projectile gets despawned.
        /// </summary>
        public GameObject explosionFX;

        public GameObject treeExplosion;

        //reference to rigidbody component
        private Rigidbody myRigidbody;
        //reference to collider component
        private SphereCollider sphereCol;
        //caching maximum count of bounces for restore
        private int maxBounce;
        //caching last bounce position for calculating next direction. Instead of using
        //the current bullet position on collision, calculating the bounce off the previous
        //bullet position improves the result for high speed bullets which could skip colliders
        private Vector3 lastBouncePos;

        /// <summary>
        /// Player gameobject that spawned this projectile.
        /// </summary>
        [HideInInspector]
        public GameObject owner;
        public int doubleRate = 100;
        public int spawnTime;


        //get component references
        void Awake()
        {
          myRigidbody = GetComponent<Rigidbody>();
          sphereCol = GetComponent<SphereCollider>();
          maxBounce = bounce;
        }


        //set initial travelling velocity
        //On Host, add automatic despawn coroutine
        void OnSpawn()
        {
          //for bouncing bullets, save current position only on first spawn (turret position)
          if (bounce == maxBounce)
            lastBouncePos = transform.position;

          myRigidbody.velocity = speed * transform.forward;
            spawnTime = (short)(Time.time * 100);
          PoolManager.Despawn(gameObject, despawnDelay);
        }

        void OnCollisionEnter(Collision collision)
        {
            //if (collision.collider)
            //GameObject.Destroy(gameObject);            
        }

         ///check what was hit on collisions. Only do non-critical client work here,
        //not even accessing player variables or anything like that. The server side is separate below
        void OnTriggerEnter(Collider col)
        {
          //cache corresponding gameobject that was hit
          GameObject obj = col.gameObject;
          //try to get a player component out of the collided gameobject
          Player player = obj.GetComponent<Player>();

            if (obj.name == "Tree")
            {
                GameObject.Destroy(obj);
                if (treeExplosion) PoolManager.Spawn(treeExplosion, obj.transform.position, Quaternion.identity);
                if (hitClip) AudioManager.Play3D(hitClip, transform.position);
            }

        
          //we actually hit a player
          //do further checks
          if (player != null)
          {
            //ignore ourselves & disable friendly fire (same team index)
            if (IsFriendlyFire(owner.GetComponent<Player>(), player)) return;

            //create clips and particles on hit
            if (hitFX) PoolManager.Spawn(hitFX, transform.position, transform.rotation, obj.transform);
            if (hitClip) AudioManager.Play3D(hitClip, transform.position);
          }
          else if (bounce > 0)
          {
            //a player was not hit but something else, and we still have some bounces left
            //create a ray that points in the direction this bullet is currently flying to
            Ray ray = new Ray(lastBouncePos - transform.forward * 0.5f, transform.forward);
            RaycastHit hit;

            //perform spherecast in the flying direction, on the default layer
            if (Physics.SphereCast(ray, sphereCol.radius, out hit, Mathf.Infinity, 1 << 0))
            {
              //ignore multiple collisions i.e. inside colliders
              if (Vector3.Distance(transform.position, lastBouncePos) < 0.05f)
              {
                return;
              }

              //cache latest collision point
              lastBouncePos = hit.point;
              //substract bouncing count by one
              bounce--;

              //something was hit in the direction this projectile is flying to
              //get new reflected (bounced off) direction of the colliding object
              Vector3 dir = Vector3.Reflect(ray.direction, hit.normal);
              //rotate bullet to face the new direction
              transform.rotation = Quaternion.LookRotation(dir);
              //reassign velocity with the new direction in mind
              OnSpawn();

              //play clip at the collided position
              if (hitClip) AudioManager.Play3D(hitClip, transform.position);
              //exit execution until next collision
              return;
            }
          }

          //despawn gameobject
          PoolManager.Despawn(gameObject);
          Player ownerPlayer = owner.GetComponent<Player>();
          //the previous code is not synced to clients at all, because all that clients need is the
          //initial position and direction of the bullet to calculate the exact same behavior on their end.
          //at this point, continue with the critical game aspects only on the server
          if (!PhotonNetwork.IsMasterClient) return;

          //create list for affected players by this bullet and add the collided player immediately,
          //we have done validation & friendly fire checks above already
          List<Player> targets = new List<Player>();
          if (player != null) targets.Add(player);

          //in case this bullet can hit more than 1 target, perform the additional physics area check
          if (maxTargets > 1)
          {
            //find all colliders in the specified range around this bullet, on the Player layer
            Collider[] others = Physics.OverlapSphere(transform.position, explosionRange, 1 << 8);

            //loop over all player collisions found
            for (int i = 0; i < others.Length; i++)
            {
              //get Player component from that collision
              Player other = others[i].GetComponent<Player>();
              if (other == null || targets.Contains(other)) continue;

              //again, ignore own bullets and also friendly fire, now done exclusively on server side
              if (IsFriendlyFire(ownerPlayer, other)) continue;

              //add this Player component to the list
              //cancel in case we do reach the maximum count now
              targets.Add(other);
              if (targets.Count == maxTargets)
                break;
            }
          }
          //apply bullet damage to the collided players
          for (int i = 0; i < targets.Count; i++)
          {
                targets[i].TakeDamage(this.damage, this.spawnTime, this.owner);
            // if(ownerPlayer.socketID != ""){
            //     SocketGameManager.instance.AddExperience(ownerPlayer.socketID, targets[i].level);
            // }

          }

        }


        //set despawn effects and reset variables
        void OnDespawn()
        {
          //create clips and particles on despawn
          if (explosionFX) PoolManager.Spawn(explosionFX, transform.position, transform.rotation);
          if (explosionClip) AudioManager.Play3D(explosionClip, transform.position);

          //reset modified variables to the initial state
          myRigidbody.velocity = Vector3.zero;
          myRigidbody.angularVelocity = Vector3.zero;
          bounce = maxBounce;
        }


        //method to check for friendly fire (same team index).
        private bool IsFriendlyFire(Player origin, Player target)
        {
          //do not trigger damage for colliding with our own bullet
          if (target.gameObject == owner || target.gameObject == null) return true;
          //perform the actual friendly fire check on both team indices and see if they match
          else if (!GameManager.GetInstance().friendlyFire && origin.GetView().GetTeam() == target.GetView().GetTeam()) return true;

          //friendly fire is off, this bullet should do damage
          return false;
        }
      }
    }
