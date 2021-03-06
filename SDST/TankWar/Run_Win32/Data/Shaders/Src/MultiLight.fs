#version 420 core

#define MAX_LIGHTS 8

// Uniforms ==============================================
// Constants
uniform vec3 EYE_POSITION;  // camera related

uniform int RENDER_MODE;

struct Light 
{
   vec4 color;    // alpha is intensity

   // data can only cross a 16-byte boundry IF it starts on a 16-byte boundary.
   vec3 position; 
   float spotFactor; 

   vec3 direction; 
   float directionFactor; 

   vec3 attenuation; 
   float dotInnerAngle; 

   vec3 specAttenuation; 
   float dotOuterAngle; 

   vec3 padding;
   float isShadowCasting;

   mat4 shadowVP;
}; 

struct LightFactor
{
   vec3 diffuse;
   vec3 specular;
};

layout(binding=3, std140) uniform uLightBlock
{
   vec4 AMBIENCE;
   Light LIGHTS[MAX_LIGHTS];
};

layout(binding=4, std140) uniform uSpecularBlock
{
   float SPECULAR_FACTOR;
   float SPECULAR_POWER;
   vec2 padding0;
};

layout(binding=5, std140) uniform uFogBlock
{
   vec4 FOG_COLOR; 
   float FOG_NEAR_PLANE;
   float FOG_FAR_PLANE;
   float FOG_NEAR_FACTOR;
   float FOG_FAR_FACTOR;
};

// Textures
// Suggest always manually setting bindings - again, consitancy with 
// other rendering APIs and well as you can make assumptions in your
// engine without having to query
layout(binding = 0) uniform sampler2D gTexDiffuse;
layout(binding = 1) uniform sampler2D gTexNormal;
layout(binding = 7) uniform sampler2DShadow gShadowMap;

// Attributes ============================================
in vec2 passUV; 
in vec4 passColor; 
in vec3 passViewPos;
in vec3 passWorldPos;   
in vec3 passWorldNormal;
in vec3 passWorldTangent;
in vec3 passWorldBiTangent;

out vec4 outColor; 

// Fog util
vec4 ApplyFog( vec4 color, float view_depth )
{
   //#if defined(FOG)
      float fog_factor = smoothstep( FOG_NEAR_PLANE, FOG_FAR_PLANE, view_depth ); 
      fog_factor = FOG_NEAR_FACTOR + (FOG_FAR_FACTOR - FOG_NEAR_FACTOR) * fog_factor; 
      color = mix( color, FOG_COLOR, fog_factor ); 
   //#endif

   return color; 
}

// Math util
float RangeMap( float x, float d0, float d1, float r0, float r1 )
{
   return (((x - d0) / (d1 - d0)) * (r1 - r0)) + r0;    
}

// Attenuation calculation
float GetAttenuation( float intensity, float dist, vec3 attenConstants )
{
   return intensity / (attenConstants.x + (attenConstants.y * dist) + (attenConstants.z * dist * dist));
}

float CalculateShadowFactor(vec3 pos, vec3 normal, Light light)
{
   if(light.isShadowCasting == 0.0f)
   {
      return 1.0f;
   }
   float bias_factor = max( dot( light.direction, normal ), 0.0f ); 
   bias_factor = sqrt(1 - (bias_factor * bias_factor)); 
   pos -= light.direction * bias_factor * .25f; 
   
   vec4 clip = light.shadowVP * vec4(pos, 1.0f);
   vec3 ndc = clip.xyz / clip.w;
   vec3 uvd = (ndc.xyz + vec3(1.0f)) * 0.5f;

   float shadowDepth = texture(gShadowMap, uvd).r;

   float min_uv = min( uvd.x, uvd.y ); 
   float max_uv = max( uvd.x, uvd.y ); 
   float blend = 1.0f - min( smoothstep(0.0f, .05f, min_uv), smoothstep(1.0, .95, max_uv) ); 

   // scale by shadow amount
   return mix( light.isShadowCasting * shadowDepth, 1.0f, blend );  
}

// Lighting calculation for one light 
LightFactor CalculateLightFactor( vec3 pos, vec3 eyeDir, vec3 normal, Light light, float specFactor, float specPower )
{
   LightFactor lightFactor;

   float shadowFactor = CalculateShadowFactor(pos, normal, light);

   // Get the direction to light and its distance
   vec3 lightDir = light.position - pos;
   float dist = length(lightDir);
   lightDir /= dist;

   // Directional Light
   vec3 lightForward = normalize(light.direction);

   float lightPower = light.color.w;

   float dotAngle = dot(lightForward, -lightDir);
   
   // Spotlights 
   float coneFactor = RangeMap(dotAngle, light.dotOuterAngle, light.dotInnerAngle, 0.0f, 1.0f);
    coneFactor = clamp( coneFactor, 0.0f, 1.0f);
   coneFactor = smoothstep(0.0f, 1.0f, coneFactor);

   // Branchless spot/directional light
   coneFactor = mix( 1.0f, coneFactor, light.spotFactor);
   
   lightPower = lightPower * coneFactor;


   // Mix between the light dir calculations to avoid branches for directional/point light
   lightDir = mix(lightDir, -lightForward, light.directionFactor);

   // Attenuation Calculation 
   float attenValue = clamp( GetAttenuation(lightPower, dist, light.attenuation), 0.0f, 1.0f);

   // Calculate DOT3 factor
   float dot3 = dot(lightDir, normal);
   float diffuseFactor = dot3 * attenValue;
   diffuseFactor = clamp(diffuseFactor, 0.0f, 1.0f);
   
   //Specular part
   vec3 reflectedLight = reflect(-lightDir, normal);
   float specAmount = max(dot(reflectedLight, eyeDir), 0.0f);
   float specIntensity = coneFactor * specFactor * attenValue * pow(specAmount, specPower);

   // Calculating light factor from the results
   vec3 lightColor = light.color.xyz;
   lightFactor.diffuse = lightColor * diffuseFactor * shadowFactor;
   lightFactor.specular = lightColor * specIntensity * shadowFactor;

   return lightFactor;
}

// Calculate lighting for all the lights
LightFactor CalculateLighting( vec3 worldPos, vec3 eyeDir, vec3 normal, float specFactor, float specPower )
{
   LightFactor finalFactor;
   finalFactor.diffuse = AMBIENCE.xyz * AMBIENCE.w; // Ambient light is always there
   finalFactor.specular = vec3(0.0f); // Specular initializes at 0

   for(int lightIndex = 0; lightIndex < MAX_LIGHTS; ++lightIndex)
   {
      LightFactor factor = CalculateLightFactor(worldPos, eyeDir, normal, LIGHTS[lightIndex], specFactor, specPower);
      finalFactor.diffuse += factor.diffuse;
      finalFactor.specular += factor.specular;
   }

   finalFactor.diffuse = clamp(finalFactor.diffuse, vec3(0.0f), vec3(1.0f));
   return finalFactor;
}

// Debug tool
vec3 WorldNormalToColor( vec3 normal )
{
   return (normal + vec3(1.0f)) * .5f; 
}

// Entry Point ===========================================
void main( void )
{
   // Texture color sampling
   vec4 texColor = texture( gTexDiffuse, passUV );

   // Normal color sampling
   vec3 normalColor = texture( gTexNormal, passUV ).xyz;

   // Interpolation is linear, so normals become not normal
   // over a surface, so renormalize it. 
   vec3 worldvNormal = normalize(passWorldNormal);

   // Get the surface to world matrix
   vec3 worldvTan = normalize(passWorldTangent); 
   vec3 worldvbitan = normalize(passWorldBiTangent); 
   mat3 surfaceToWorld = mat3( worldvTan, worldvbitan, worldvNormal ); 

   vec3 surfaceNormal = normalize( normalColor * vec3( 2.0f, 2.0f, 1.0f ) + vec3( -1.0f, -1.0f, 0.0f ) );
   vec3 worldNormal = surfaceToWorld * surfaceNormal; // TBN transformation

   // Color of this surface
   vec4 surfaceColor = texColor * passColor; 

   // used in final lighting equation to compute
   // final color of output - calculated from the light
   vec3 surface_light = vec3(0); // How much light is hitting the surface
   vec3 reflected_light = vec3(0);  // How much light is reflected back

   // First, some vectors we'll use
   vec3 eyeDir = normalize(EYE_POSITION - passWorldPos); // direction to the eye

   // Calculating the lightFactor
   LightFactor factor = CalculateLighting( passWorldPos, eyeDir, worldNormal, SPECULAR_FACTOR, SPECULAR_POWER);

   vec4 final_color = vec4(factor.diffuse, 1) * surfaceColor + vec4(factor.specular, 0); 

   // Reclamp so that we're in a valid colour range.  May want to save off overflow
   // if doing bloom.
   final_color = clamp(final_color, vec4(0), vec4(1) ); 
   final_color = ApplyFog(final_color, passViewPos.z);

   switch(RENDER_MODE)
   {
      case 0: outColor = final_color; break; // Default lit
      case 1: outColor = vec4(WorldNormalToColor(worldvNormal), 1); break;
      case 2: outColor = vec4(WorldNormalToColor(worldvTan), 1); break;
      case 3: outColor = vec4(WorldNormalToColor(worldvbitan),1); break;
      case 4: outColor = vec4(WorldNormalToColor(surfaceNormal), 1); break;
      case 5: outColor = vec4(WorldNormalToColor(worldNormal), 1.0f); break;
      case 6: outColor = ApplyFog(vec4(1), passViewPos.z); break;
      case 7: outColor = vec4(CalculateShadowFactor(passWorldPos, worldNormal, LIGHTS[0])); break;
      default: outColor = surfaceColor; break;
   }
   
}